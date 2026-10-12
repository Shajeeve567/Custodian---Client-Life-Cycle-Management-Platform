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
/// Reports built from Workflow's live data (CSTD-36 convention, see docs/reporting.md). Owner/Staff only;
/// the tenant always comes from the JWT.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Roles = "Owner,Staff")]
public class ReportsController : ControllerBase
{
    private readonly ISlaPerformanceReportService _slaReport;
    private readonly IReadinessHandoffReportService _readinessReport;
    private readonly IReportRenderer _renderer;
    private readonly ICsvExporter _csv;
    private readonly ReportTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    public ReportsController(
        ISlaPerformanceReportService slaReport,
        IReadinessHandoffReportService readinessReport,
        IReportRenderer renderer,
        ICsvExporter csv,
        ReportTelemetry telemetry,
        TimeProvider timeProvider)
    {
        _slaReport = slaReport;
        _readinessReport = readinessReport;
        _renderer = renderer;
        _csv = csv;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// CSTD-37: SLA performance (on-time/late/overdue, timing, breakdowns, stall statistics) as a PDF,
    /// or the per-action detail as CSV (<c>format=csv</c>).
    /// Filters: from/to (yyyy-MM-dd, default the last 30 days), engagementId, stage (1–5 or name),
    /// staffId, actionType. Errors are ProblemDetails: 400 bad filter, 404 engagement not in tenant.
    /// </summary>
    [HttpGet("sla-performance")]
    [ReportErrors(SlaPerformanceReportBuilder.ReportCode)]
    public async Task<IActionResult> SlaPerformance([FromQuery] SlaReportQuery query, CancellationToken ct)
    {
        ReportAuthorization.EnsureStaffOrOwner(User);
        var tenantId = ReportAuthorization.RequireTenantId(User);
        var format = ReportFormats.Parse(query.Format);
        var now = _timeProvider.GetUtcNow(); // one "now" for the filters, the maths and the header

        var output = await _telemetry.MeasureAsync(SlaPerformanceReportBuilder.ReportCode, format, async () =>
        {
            var filter = SlaReportFilter.Parse(query, now);
            // Staff report only on the engagements they are responsible for; Owners on the whole workspace.
            var data = await _slaReport.ComputeAsync(tenantId, filter, now, EngagementAccess.RestrictedStaffId(User), ct);
            var report = SlaPerformanceReportBuilder.Build(data, tenantId, ReportAuthorization.ResolveActor(User));

            var bytes = format == ReportFormat.Csv ? _csv.ToCsv(report.Detail) : _renderer.RenderPdf(report);
            return new ReportRun(ReportResults.Output(report.Metadata, format, bytes), data.IsEmpty);
        });

        return ReportResults.File(output);
    }
    
    /// <summary>
    /// CSTD-38: Delivery Readiness & Handoff Summary for one engagement. Reuses CSTD-20's
    /// readiness result and the persisted handoff/closure context; renders a PDF through the
    /// shared reporting pipeline. Owner sees any engagement in the tenant; Staff only engagements
    /// they are responsible for (a 404 otherwise, same as any other staff-restricted read).
    /// Errors are ProblemDetails: 400 invalid engagement id, 404 engagement not in tenant, 503
    /// database unreachable. The frontend passes <c>?format=pdf</c> for parity with the other
    /// report endpoints; any additional query params are ignored (PDF only).
    /// </summary>
    [HttpGet("delivery-readiness-handoff")]
    [ReportErrors(ReadinessHandoffReportBuilder.ReportCode)]
    public async Task<IActionResult> DeliveryReadinessHandoff(
        [FromQuery] string? engagementId,
        CancellationToken ct)
    {
        ReportAuthorization.EnsureStaffOrOwner(User);
        var tenantId = ReportAuthorization.RequireTenantId(User);

        if (!Guid.TryParse(engagementId, out var id) || id == Guid.Empty)
        {
            throw ReportGenerationException.InvalidFilter(
                "engagementId", "'engagementId' is required and must be a valid engagement id.");
        }

        var now = _timeProvider.GetUtcNow();
        var restrictToStaffId = EngagementAccess.RestrictedStaffId(User);

        var output = await _telemetry.MeasureAsync(ReadinessHandoffReportBuilder.ReportCode, ReportFormat.Pdf, async () =>
        {
            var data = await _readinessReport.ComputeAsync(tenantId, id, now, restrictToStaffId, ct);
            var report = ReadinessHandoffReportBuilder.Build(data, tenantId, ReportAuthorization.ResolveActor(User));
            var bytes = _renderer.RenderPdf(report);
            return new ReportRun(ReportResults.Output(report.Metadata, ReportFormat.Pdf, bytes), IsEmpty: false);
        });

        return ReportResults.File(output);
    }
}
