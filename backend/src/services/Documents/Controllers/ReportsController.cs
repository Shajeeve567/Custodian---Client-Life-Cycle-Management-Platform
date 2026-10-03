using Custodian.Documents.Services.Reports;
using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Documents.Controllers;

/// <summary>
/// CSTD-182: Reports built from Documents service live data. Owner/Staff only;
/// the tenant always comes from the authenticated JWT.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Roles = "Owner,Staff")]
public class ReportsController : ControllerBase
{
    private readonly IValidationVerificationReportService _reportService;
    private readonly IReportEngagementScopeResolver _scopeResolver;
    private readonly IReportRenderer _renderer;
    private readonly ICsvExporter _csv;
    private readonly ReportTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    public ReportsController(
        IValidationVerificationReportService reportService,
        IReportEngagementScopeResolver scopeResolver,
        IReportRenderer renderer,
        ICsvExporter csv,
        ReportTelemetry telemetry,
        TimeProvider timeProvider)
    {
        _reportService = reportService;
        _scopeResolver = scopeResolver;
        _renderer = renderer;
        _csv = csv;
        _telemetry = telemetry;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// CSTD-182: Validation &amp; Verification report as PDF or CSV (<c>format=csv</c>).
    /// Filters: from/to (yyyy-MM-dd), engagementId.
    /// Errors are RFC 7807 ProblemDetails: 400 bad filter, 403 forbidden, 503 data source unavailable.
    /// </summary>
    [HttpGet("validation-verification")]
    [ReportErrors(ValidationVerificationReportBuilder.ReportCode)]
    public async Task<IActionResult> ValidationVerification([FromQuery] ValidationVerificationReportQuery query, CancellationToken ct)
    {
        ReportAuthorization.EnsureStaffOrOwner(User);
        var tenantId = ReportAuthorization.RequireTenantId(User);
        var format = ReportFormats.Parse(query.Format);
        var now = _timeProvider.GetUtcNow();

        var output = await _telemetry.MeasureAsync(ValidationVerificationReportBuilder.ReportCode, format, async () =>
        {
            var filter = query.ToFilter();
            var allowedEngagementIds = await _scopeResolver.ResolveAllowedEngagementIdsAsync(User, tenantId, ct);
            var data = await _reportService.ComputeAggregateAsync(tenantId, filter, allowedEngagementIds, ct);
            var report = ValidationVerificationReportBuilder.Build(data, tenantId, ReportAuthorization.ResolveActor(User), filter, now);

            var bytes = format == ReportFormat.Csv ? _csv.ToCsv(report.Detail) : _renderer.RenderPdf(report);
            return new ReportRun(ReportResults.Output(report.Metadata, format, bytes), data.IsEmpty);
        });

        return ReportResults.File(output);
    }
}
