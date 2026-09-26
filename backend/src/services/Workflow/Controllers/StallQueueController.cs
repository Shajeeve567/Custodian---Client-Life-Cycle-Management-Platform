using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize(Roles = "Owner,Staff")]
[ApiController]
[Route("api/stall-queue")]
public class StallQueueController : ControllerBase
{
    private readonly IStallQueueService _queueService;

    public StallQueueController(IStallQueueService queueService)
    {
        _queueService = queueService;
    }

    /// <summary>
    /// Returns the tenant's stalled engagements ordered by urgency
    /// (most overdue first). Owner/Staff only. Empty array when nothing
    /// is stalled — never 404.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StallQueueItemDto>>> GetQueue(
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
            return BadRequest(new { message = "Tenant identification is required." });

        var items = await _queueService.GetQueueAsync(effectiveTenantId);
        return Ok(items);
    }

    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? queryTenantId)
    {
        var jwtClaimTenant = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaimTenant))
        {
            var cleanJwtTenant = jwtClaimTenant.Trim();

            if (!string.IsNullOrWhiteSpace(queryTenantId) &&
                !string.Equals(queryTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            if (Request?.Headers != null &&
                Request.Headers.TryGetValue("X-Tenant-ID", out var headerVal))
            {
                var headerTenant = headerVal.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(headerTenant) &&
                    !string.Equals(headerTenant, cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, true);
                }
            }

            return (cleanJwtTenant, false);
        }

        if (Request?.Headers != null &&
            Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            var headerTenant = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerTenant))
            {
                return (headerTenant.Trim(), false);
            }
        }

        if (!string.IsNullOrWhiteSpace(queryTenantId))
        {
            return (queryTenantId.Trim(), false);
        }

        return (null, false);
    }
}