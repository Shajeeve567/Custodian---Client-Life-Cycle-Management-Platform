using System.Data.Common;
using Custodian.Shared.Reporting.Errors;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Reports;

public interface IReadinessHandoffReportService
{
    /// <summary>
    /// CSTD-38: compiles the report data for one engagement. Readiness comes from CSTD-20's
    /// service (never re-derived); documents from the Documents client; conditions from the local
    /// DB; handoff/closure context from the engagement row. Throws
    /// <see cref="ReportGenerationException"/> 404 for a missing or staff-restricted engagement,
    /// 503 when the database is unreachable.
    /// </summary>
    Task<ReadinessHandoffReportData> ComputeAsync(
        string tenantId,
        Guid engagementId,
        DateTimeOffset now,
        string? restrictToStaffId = null,
        CancellationToken ct = default);
}

/// <summary>
/// CSTD-38 implementation. The CSTD-20 readiness result is the single source of truth — this
/// service only assembles it together with the supporting evidence and the persisted handoff
/// context. Unavailable dependencies are represented, never hidden: an unreachable Documents
/// service produces a document section marked "unavailable" and forces <see cref="ReadinessHandoffReportData.IsReady"/>
/// to false. An unreachable readiness service is represented similarly (no verdict, IsReady false).
/// </summary>
public sealed class ReadinessHandoffReportService : IReadinessHandoffReportService
{
    private readonly WorkflowDbContext _db;
    private readonly IEngagementRepository _engagements;
    private readonly IReadinessService _readiness;
    private readonly IDocumentComplianceClient _documentClient;
    private readonly ILogger<ReadinessHandoffReportService> _logger;

    public ReadinessHandoffReportService(
        WorkflowDbContext db,
        IEngagementRepository engagements,
        IReadinessService readiness,
        IDocumentComplianceClient documentClient,
        ILogger<ReadinessHandoffReportService> logger)
    {
        _db = db;
        _engagements = engagements;
        _readiness = readiness;
        _documentClient = documentClient;
        _logger = logger;
    }

    public async Task<ReadinessHandoffReportData> ComputeAsync(
        string tenantId,
        Guid engagementId,
        DateTimeOffset now,
        string? restrictToStaffId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        try
        {
            var engagement = await _engagements.GetByIdAsync(engagementId, tenantId)
                ?? throw ReportGenerationException.SubjectNotFound(
                    "The selected engagement was not found in this workspace.");

            // Same answer for a staff-restricted engagement as for a missing one, so existence isn't leaked.
            if (restrictToStaffId is not null &&
                !string.Equals(engagement.StaffId?.Trim(), restrictToStaffId, StringComparison.OrdinalIgnoreCase))
            {
                throw ReportGenerationException.SubjectNotFound(
                    "The selected engagement was not found in this workspace.");
            }

            var (readiness, readinessUnavailable) =
                await TryReadinessAsync(engagementId, tenantId, ct);

            var (documents, documentsUnavailable) =
                await TryDocumentsAsync(engagementId, tenantId, ct);

            var conditions = await LoadConditionsAsync(engagementId, tenantId, ct);

            var approvalConditions = conditions
                .Where(c => string.Equals(c.Type, ConditionType.Approval, StringComparison.OrdinalIgnoreCase))
                .Select(ToReportCondition)
                .ToList();

            var paymentConditions = conditions
                .Where(c => string.Equals(c.Type, ConditionType.Payment, StringComparison.OrdinalIgnoreCase))
                .Select(ToReportCondition)
                .ToList();

            return new ReadinessHandoffReportData(
                GeneratedAtUtc: now.ToUniversalTime(),
                Engagement: new ReadinessHandoffEngagement(
                    engagement.EngagementId,
                    engagement.TenantId,
                    engagement.ClientId,
                    engagement.StaffId!,
                    engagement.Stage.ToString(),
                    engagement.Status.ToString(),
                    engagement.CreatedAt,
                    engagement.ClosedAt),
                Readiness: readiness,
                ReadinessUnavailable: readinessUnavailable,
                Documents: documents,
                DocumentsUnavailable: documentsUnavailable,
                ApprovalConditions: approvalConditions,
                PaymentConditions: paymentConditions,
                Handoff: BuildHandoffContext(engagement),
                Closure: BuildClosureContext(engagement));
        }
        catch (DbException ex)
        {
            throw ReportGenerationException.DataSourceUnavailable(
                "The Workflow database is not reachable right now.", ex);
        }
    }

    private async Task<(ReadinessResponse? Result, bool Unavailable)> TryReadinessAsync(
        Guid engagementId, string tenantId, CancellationToken ct)
    {
        try
        {
            return (await _readiness.EvaluateAsync(engagementId, tenantId, ct), false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Readiness service unavailable while generating CSTD-38 report for engagement {EngagementId}", engagementId);
            return (null, true);
        }
    }

    private async Task<(IReadOnlyList<ReadinessHandoffDocument> Documents, bool Unavailable)> TryDocumentsAsync(
        Guid engagementId, string tenantId, CancellationToken ct)
    {
        try
        {
            var docs = await _documentClient.GetDocumentsAsync(engagementId, tenantId, ct);
            return (docs.Select(ToReportDocument).ToList(), false);
        }
        catch (DocumentComplianceUnavailableException ex)
        {
            _logger.LogWarning(ex,
                "Documents service unavailable while generating CSTD-38 report for engagement {EngagementId}", engagementId);
            return (Array.Empty<ReadinessHandoffDocument>(), true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error loading documents for CSTD-38 report on engagement {EngagementId}", engagementId);
            return (Array.Empty<ReadinessHandoffDocument>(), true);
        }
    }

    private async Task<List<EngagementCondition>> LoadConditionsAsync(
        Guid engagementId, string tenantId, CancellationToken ct)
    {
        return await _db.EngagementConditions
            .AsNoTracking()
            .Where(c => c.EngagementId == engagementId && c.TenantId == tenantId)
            .OrderBy(c => c.RequiredBeforeStage)
            .ThenBy(c => c.CreatedAt)
            .ToListAsync(ct);
    }

    private static ReadinessHandoffDocument ToReportDocument(DocumentSummaryDto d) => new(
        d.DocumentId, d.Type, d.ComplianceStatus, d.VerificationStatus,
        VerifiedBy: null, VerifiedAt: null, Notes: d.RejectionReason);

    private static ReadinessHandoffCondition ToReportCondition(EngagementCondition c) => new(
        c.ConditionId, c.Type, c.Title, c.Status,
        ApprovalStatus: DeriveApprovalStatus(c),
        c.Amount, c.Currency, c.PaymentType,
        c.DueDateUtc, c.SatisfiedAt, c.SatisfiedBy, c.RejectionReason, c.IsActive);

    private static string? DeriveApprovalStatus(EngagementCondition c)
    {
        if (!string.Equals(c.Type, ConditionType.Approval, StringComparison.OrdinalIgnoreCase)) return null;
        return c.Status switch
        {
            ConditionStatus.Pending => "Pending",
            ConditionStatus.Satisfied => "Approved",
            ConditionStatus.Rejected => "Rejected",
            _ => c.Status
        };
    }

    private static ReadinessHandoffContext? BuildHandoffContext(Engagement e) =>
        e.HandedOffAtUtc is null
            ? null
            : new ReadinessHandoffContext(
                e.HandoffSummary ?? string.Empty,
                e.HandedOffBy ?? string.Empty,
                e.HandedOffAtUtc.Value);

    private static ReadinessClosureContext? BuildClosureContext(Engagement e) =>
        e.ClosedAt is null
            ? null
            : new ReadinessClosureContext(
                e.ClosureSummary,
                e.ClosedBy ?? string.Empty,
                e.ClosedAt.Value);
}