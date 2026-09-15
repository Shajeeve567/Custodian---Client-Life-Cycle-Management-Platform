using Custodian.Shared.Reporting.Reports;
using Custodian.Workflow.Models;
using Custodian.Workflow.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

/// <summary>
/// Dynamic engagement reporting. Restricted to Owner/Staff — a report listing every
/// engagement's ClientId/StaffId/Status/Stage across the tenant is staff-facing data by
/// nature, not something a Client-role caller should ever be able to export.
/// </summary>
[Authorize(Roles = "Owner,Staff")]
[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IEngagementRepository _repository;
    private readonly ReportGenerator _reportGenerator;

    public ReportsController(
        IEngagementRepository repository,
        ReportGenerator reportGenerator)
    {
        _repository = repository;
        _reportGenerator = reportGenerator;
    }

    [HttpGet("engagements")]
    public async Task<IActionResult> GetEngagementReport(
        [FromQuery] string? tenantId,
        [FromQuery] string format = "csv")
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim or tenantId parameter." });
        }

        var engagements = await _repository.GetAllByTenantAsync(effectiveTenantId);

        var definition = new ReportDefinition<Engagement>(
            "engagement-summary",
            [
                new("Engagement ID", item => item.EngagementId),
                new("Client ID", item => item.ClientId),
                new("Staff ID", item => item.StaffId),
                new("Status", item => item.Status),
                new("Stage", item => item.Stage),
                new("Created At", item => item.CreatedAt),
                new("Closed At", item => item.ClosedAt)
            ]);

        IActionResult BadFormat() => BadRequest(new { message = "Format must be csv or json." });

        var report = format.ToLowerInvariant() switch
        {
            "csv" => _reportGenerator.GenerateCsv(definition, engagements),
            "json" => _reportGenerator.GenerateJson(definition, engagements),
            _ => null
        };

        if (report == null)
        {
            return BadFormat();
        }

        return File(
            report.Content,
            report.ContentType,
            $"{report.Name}.{format.ToLowerInvariant()}");
    }

    /// <summary>
    /// Resolves tenant ID server-side from the JWT claim and strictly rejects a caller who
    /// specifies a different tenantId than their own claim — the same tenant-isolation
    /// business rule enforced everywhere else in this service. Note: EngagementsController/
    /// ClientActionsController on this branch still use the older, non-rejecting
    /// ResolveTenantId (prefer claim, silently fall back to the raw param with no mismatch
    /// check) — upgrading them to this stricter behavior is a separate, pre-existing gap,
    /// out of scope for the reporting fix.
    /// </summary>
    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? requestTenantId)
    {
        var jwtTenantId = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtTenantId))
        {
            var cleanJwtTenant = jwtTenantId.Trim();
            if (!string.IsNullOrWhiteSpace(requestTenantId) &&
                !string.Equals(requestTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            return (cleanJwtTenant, false);
        }

        return (!string.IsNullOrWhiteSpace(requestTenantId) ? requestTenantId.Trim() : null, false);
    }
}
