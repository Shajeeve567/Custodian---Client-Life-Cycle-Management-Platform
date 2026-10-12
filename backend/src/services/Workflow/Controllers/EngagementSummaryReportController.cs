using System.Security.Claims;
using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Custodian.Workflow.Services.Access;
using Custodian.Workflow.Services.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

/// <summary>
/// CSTD-14: the engagement summary report as a PDF download (CSTD-36 convention, see docs/reporting.md).
/// Tenant, client and staff identity come from the JWT only. Every denial of a specific engagement
/// (unknown, other tenant, another client's, not assigned to the Staff caller) is the same 404.
/// </summary>
[ApiController]
[ReportErrors(EngagementSummaryReportBuilder.ReportCode)]
public class EngagementSummaryReportController : ControllerBase
{
    private readonly IEngagementSummaryReportService _summary;
    private readonly IReportRenderer _renderer;
    private readonly ReportTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    public EngagementSummaryReportController(
        IEngagementSummaryReportService summary,
        IReportRenderer renderer,
        ReportTelemetry telemetry,
        TimeProvider timeProvider)
    {
        _summary = summary;
        _renderer = renderer;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
    }

    /// <summary>The client's own engagement summary (client-safe content only).</summary>
    [HttpGet("api/portal/engagements/{engagementId:guid}/summary-report")]
    [Authorize(Roles = "Client")]
    public Task<IActionResult> ClientSummary([FromRoute] Guid engagementId, CancellationToken ct)
    {
        var clientId = CallerClientId(User)
            ?? throw ReportGenerationException.Forbidden("Your sign-in has no client identity.");
        return GenerateAsync(engagementId, EngagementSummaryAudience.Client, clientId, restrictedStaffId: null, ct);
    }

    /// <summary>
    /// The same report for an Owner (any engagement in the workspace) or the responsible Staff member,
    /// with a staff-details section added.
    /// </summary>
    [HttpGet("api/reports/engagement-summary/{engagementId:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public Task<IActionResult> StaffSummary([FromRoute] Guid engagementId, CancellationToken ct)
    {
        ReportAuthorization.EnsureStaffOrOwner(User);
        return GenerateAsync(engagementId, EngagementSummaryAudience.Staff, clientId: null, EngagementAccess.RestrictedStaffId(User), ct);
    }

    private async Task<IActionResult> GenerateAsync(
        Guid engagementId, EngagementSummaryAudience audience, string? clientId, string? restrictedStaffId, CancellationToken ct)
    {
        var tenantId = ReportAuthorization.RequireTenantId(User);
        var now = _timeProvider.GetUtcNow(); // one "now" for the data and the header

        var output = await _telemetry.MeasureAsync(EngagementSummaryReportBuilder.ReportCode, ReportFormat.Pdf, async () =>
        {
            var request = new EngagementSummaryRequest(
                engagementId, tenantId, audience, clientId, restrictedStaffId, ReportAuthorization.ResolveActor(User), now);
            var data = await _summary.ComputeAsync(request, ct);
            var report = EngagementSummaryReportBuilder.Build(data, tenantId);
            var bytes = _renderer.RenderPdf(report);
            var isEmpty = data.OutstandingActions.Count == 0 && data.RecentlyCompletedActions.Count == 0 &&
                          data.Approvals.Count == 0 && data.Payments.Count == 0 && data.Documents.Count == 0;
            return new ReportRun(ReportResults.Output(report.Metadata, ReportFormat.Pdf, bytes, report.EngagementCode), isEmpty);
        });

        return ReportResults.File(output);
    }

    /// <summary>
    /// The Client caller's id: the <c>client_id</c> claim Identity issues to clients, else the user id
    /// (the same rule as the portal). Never a query string or header value.
    /// </summary>
    public static string? CallerClientId(ClaimsPrincipal user)
    {
        var id = user.FindFirst("client_id")?.Value
                 ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? user.FindFirst("sub")?.Value;
        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }
}
