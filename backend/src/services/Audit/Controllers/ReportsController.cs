using System.Security.Claims;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.Reports;
using Custodian.Shared.Auth;
using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Audit.Controllers;

/// <summary>
/// Query parameters for GET /api/reports/proof-of-delivery.
/// </summary>
public sealed class ProofOfDeliveryReportQuery
{
    public string? EngagementId { get; set; }
    public string? Format { get; set; }
}

/// <summary>
/// CSTD-249: Proof-of-Delivery reporting controller.
/// Generates authorized, tenant-scoped Proof-of-Delivery PDF reports backed by
/// cryptographic SHA-256 hash-chain verification and Workflow access control.
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Roles = "Owner,Staff")]
public class ReportsController : ControllerBase
{
    private readonly IProofOfDeliveryReportService _reportService;
    private readonly IEngagementAccessClient? _engagementAccess;
    private readonly IReportRenderer _renderer;
    private readonly ReportTelemetry _telemetry;

    public ReportsController(
        IProofOfDeliveryReportService reportService,
        IReportRenderer renderer,
        ReportTelemetry telemetry,
        IEngagementAccessClient? engagementAccess = null)
    {
        _reportService = reportService;
        _renderer = renderer;
        _telemetry = telemetry;
        _engagementAccess = engagementAccess;
    }

    /// <summary>
    /// GET /api/reports/proof-of-delivery?engagementId={guid}&amp;format=pdf
    /// Produces a downloadable PDF certificate containing engagement summary, sequence-ordered
    /// audit events, truncated SHA-256 hash excerpts, cryptographic integrity verification,
    /// live-head consistency status, and exact verified history boundaries.
    /// </summary>
    [HttpGet("proof-of-delivery")]
    [ReportErrors(ProofOfDeliveryReportBuilder.ReportCode)]
    public async Task<IActionResult> ProofOfDelivery(
        [FromQuery] ProofOfDeliveryReportQuery query,
        CancellationToken ct)
    {
        // 1. Authorization: Only Owner and Staff roles are permitted.
        ReportAuthorization.EnsureStaffOrOwner(User);

        // 2. Tenant isolation: Must come from authenticated JWT tenant_id claim.
        var tenantIdString = ReportAuthorization.RequireTenantId(User);
        if (!Guid.TryParse(tenantIdString, out var tenantId) || tenantId == Guid.Empty)
        {
            throw ReportGenerationException.Forbidden("Sign in to a valid workspace to run reports.");
        }

        // 3. Validation: Engagement ID is mandatory and must be a valid GUID.
        if (string.IsNullOrWhiteSpace(query.EngagementId))
        {
            throw ReportGenerationException.InvalidFilter("engagementId", "Engagement ID is required.");
        }

        if (!Guid.TryParse(query.EngagementId, out var engagementId) || engagementId == Guid.Empty)
        {
            throw ReportGenerationException.InvalidFilter("engagementId", "Engagement ID must be a valid non-empty GUID.");
        }

        // 4. Validation: Format must be PDF.
        var format = ReportFormats.Parse(query.Format);
        if (format != ReportFormat.Pdf)
        {
            throw ReportGenerationException.InvalidFilter("format", "Proof-of-delivery report is only available in PDF format.");
        }

        // 5. Staff access restriction: Staff members can only generate reports for their assigned engagements.
        var isStaffRestricted = User.IsInRole(nameof(Role.Staff)) && !User.IsInRole(nameof(Role.Owner));
        if (isStaffRestricted && _engagementAccess != null)
        {
            try
            {
                var canAccess = await _engagementAccess.CanAccessEngagementAsync(engagementId, tenantId, ct);
                if (!canAccess)
                {
                    throw ReportGenerationException.SubjectNotFound($"Engagement '{engagementId}' was not found in the tenant.");
                }
            }
            catch (EngagementAccessUnavailableException ex)
            {
                throw ReportGenerationException.DataSourceUnavailable("Workflow service is unreachable. Unable to verify engagement access.", ex);
            }
        }

        var actor = ReportAuthorization.ResolveActor(User);

        // 6. Report generation, live verification, telemetry measurement, and PDF rendering.
        var output = await _telemetry.MeasureAsync(ProofOfDeliveryReportBuilder.ReportCode, format, async () =>
        {
            var report = await _reportService.BuildReportAsync(tenantId, engagementId, actor, ct);
            var bytes = _renderer.RenderPdf(report);
            var isEmpty = report.Sections.OfType<EmptySection>().Any();
            return new ReportRun(ReportResults.Output(report.Metadata, format, bytes), isEmpty);
        });

        // 7. Secure download response with Cache-Control: no-store and Content-Disposition.
        return ReportResults.File(output);
    }
}
