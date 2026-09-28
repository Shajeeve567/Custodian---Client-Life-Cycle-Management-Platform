using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
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
    private readonly IReportRenderer _renderer;
    private readonly ICsvExporter _csv;
    private readonly ReportTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    public ReportsController(
        ISlaPerformanceReportService slaReport,
        IReportRenderer renderer,
        ICsvExporter csv,
        ReportTelemetry telemetry,
        TimeProvider timeProvider)
    {
        _slaReport = slaReport;
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
            var data = await _slaReport.ComputeAsync(tenantId, filter, now, ct);
            var report = SlaPerformanceReportBuilder.Build(data, tenantId, ReportAuthorization.ResolveActor(User));

            var bytes = format == ReportFormat.Csv ? _csv.ToCsv(report.Detail) : _renderer.RenderPdf(report);
            return new ReportRun(ReportResults.Output(report.Metadata, format, bytes), data.IsEmpty);
        });

        return ReportResults.File(output);
    }
}
