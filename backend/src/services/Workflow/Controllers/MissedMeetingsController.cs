using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize(Roles = "Owner,Staff")]
[ApiController]
[Route("api/meetings")]
public class MissedMeetingsController : ControllerBase
{
    private readonly IMeetingService _meetings;

    public MissedMeetingsController(IMeetingService meetings)
    {
        _meetings = meetings;
    }

    /// <summary>
    /// Tenant-wide list of Important meetings that are either explicitly marked
    /// Missed or past-due while still Scheduled. Owner/Staff only.
    /// </summary>
    [HttpGet("missed")]
    public async Task<ActionResult<IReadOnlyList<MeetingResponse>>> Missed(
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
            return BadRequest(new { message = "Tenant identification is required." });

        var result = await _meetings.ListMissedForTenantAsync(effectiveTenantId, ct);
        return Ok(result);
    }

    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? queryTenantId)
    {
        var jwtTenantId = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtTenantId))
        {
            var cleanJwtTenant = jwtTenantId.Trim();
            if (!string.IsNullOrWhiteSpace(queryTenantId)
                && !string.Equals(queryTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
                return (null, true);
            return (cleanJwtTenant, false);
        }

        return (!string.IsNullOrWhiteSpace(queryTenantId) ? queryTenantId.Trim() : null, false);
    }
}