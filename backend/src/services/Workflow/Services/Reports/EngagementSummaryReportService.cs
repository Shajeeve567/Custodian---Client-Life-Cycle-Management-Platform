using Custodian.Shared.Reporting.Errors;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Reports;

/// <summary>
/// Who is asking for an engagement summary. Built by the controller from the JWT only: tenant, client id
/// and staff restriction never come from the query string, a header or the body.
/// </summary>
/// <param name="ClientId">Client route: the caller's client id. Null on the staff route.</param>
/// <param name="RestrictedStaffId">Staff route: the Staff caller's id (only their assigned engagements); null for an Owner.</param>
public sealed record EngagementSummaryRequest(
    Guid EngagementId,
    string TenantId,
    EngagementSummaryAudience Audience,
    string? ClientId,
    string? RestrictedStaffId,
    string PreparedFor,
    DateTimeOffset Now);

public interface IEngagementSummaryReportService
{
    /// <summary>
    /// Builds the summary from live Workflow and Documents data.
    /// Throws <see cref="ReportGenerationException"/>: SubjectNotFound when the engagement is not in the
    /// tenant or not the caller's (one answer for both, so existence is not revealed); DataSourceUnavailable
    /// when Documents (or the condition/document status behind the next action) cannot be read.
    /// </summary>
    Task<EngagementSummaryData> ComputeAsync(EngagementSummaryRequest request, CancellationToken ct = default);
}

/// <summary>
/// CSTD-76: the portal-safe aggregation. Each source is read through the existing client-safe path:
/// the portal dashboard (status, stage, progress, next/pending actions), the client view of actions,
/// the client-safe condition mapper and Documents' REST API. No other service's database is read.
/// </summary>
public sealed class EngagementSummaryReportService : IEngagementSummaryReportService
{
    public const string NotFoundMessage = "Engagement not found.";
    public const string DocumentsUnavailableMessage =
        "Document status is temporarily unavailable, so the summary cannot be produced. Please try again shortly.";

    private readonly WorkflowDbContext _dbContext;
    private readonly IClientPortalService _portal;
    private readonly IClientActionService _actions;
    private readonly IDocumentComplianceClient _documents;
    private readonly ILogger<EngagementSummaryReportService> _logger;

    public EngagementSummaryReportService(
        WorkflowDbContext dbContext,
        IClientPortalService portal,
        IClientActionService actions,
        IDocumentComplianceClient documents,
        ILogger<EngagementSummaryReportService> logger)
    {
        _dbContext = dbContext;
        _portal = portal;
        _actions = actions;
        _documents = documents;
        _logger = logger;
    }

    public async Task<EngagementSummaryData> ComputeAsync(EngagementSummaryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TenantId))
        {
            throw ReportGenerationException.Forbidden("Sign in to a workspace to download reports.");
        }

        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == request.EngagementId && e.TenantId == request.TenantId, ct);

        if (engagement is null || !CallerMayRead(engagement, request))
        {
            throw ReportGenerationException.SubjectNotFound(NotFoundMessage);
        }

        // Documents first: if it is down there is no report, rather than one missing its documents.
        IReadOnlyList<DocumentSummaryDto> documents;
        try
        {
            documents = await _documents.GetDocumentsAsync(engagement.EngagementId, engagement.TenantId, ct);
        }
        catch (DocumentComplianceUnavailableException ex)
        {
            throw ReportGenerationException.DataSourceUnavailable(DocumentsUnavailableMessage, ex);
        }

        var clientId = request.Audience == EngagementSummaryAudience.Client ? request.ClientId : null;
        var dashboard = await _portal.GetDashboardForEngagementAsync(engagement.EngagementId, engagement.TenantId, clientId)
            ?? throw ReportGenerationException.SubjectNotFound(NotFoundMessage);

        // The next-action engine reports a source it could not read as an "Unavailable" item; the next
        // step would then be a placeholder, so this is a dependency failure too.
        if (HasUnavailableItem(dashboard.NextAction))
        {
            _logger.LogWarning("Engagement summary for {EngagementId}: next action has unavailable inputs.", engagement.EngagementId);
            throw ReportGenerationException.DataSourceUnavailable(DocumentsUnavailableMessage);
        }

        var actions = (await _actions.GetActionsByEngagementAsync(engagement.EngagementId, engagement.TenantId, isClientView: true)).ToList();

        // Same rows as the client condition view (active only, stage then creation order), through its mapper.
        var conditions = await _dbContext.EngagementConditions
            .AsNoTracking()
            .Where(c => c.EngagementId == engagement.EngagementId && c.TenantId == engagement.TenantId && c.IsActive)
            .OrderBy(c => c.RequiredBeforeStage)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return EngagementSummaryAggregator.Aggregate(new EngagementSummaryInputs
        {
            Engagement = engagement,
            Dashboard = dashboard,
            ClientVisibleActions = actions,
            Conditions = conditions
                .Select(c => new ClientSafeConditionWithCompletion(ConditionService.MapToClientSafeDto(c), c.SatisfiedAt))
                .ToList(),
            Documents = documents,
            Audience = request.Audience,
            PreparedFor = request.PreparedFor,
            Now = request.Now
        });
    }

    /// <summary>
    /// Client: only their own engagement (the portal's client_id rule). Staff: only engagements they are
    /// responsible for. Owner: any engagement in the tenant (already enforced by the query).
    /// </summary>
    public static bool CallerMayRead(Models.Engagement engagement, EngagementSummaryRequest request) =>
        request.Audience switch
        {
            EngagementSummaryAudience.Client =>
                !string.IsNullOrWhiteSpace(request.ClientId) &&
                string.Equals(engagement.ClientId, request.ClientId.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => request.RestrictedStaffId is null ||
                 string.Equals(engagement.StaffId?.Trim(), request.RestrictedStaffId, StringComparison.OrdinalIgnoreCase)
        };

    private static bool HasUnavailableItem(NextActionResult? result) =>
        result is not null &&
        result.Blockers.Prepend(result.PrimaryAction).OfType<NextActionItem>()
            .Any(i => string.Equals(i.Kind, NextActionKind.Unavailable, StringComparison.OrdinalIgnoreCase));
}
