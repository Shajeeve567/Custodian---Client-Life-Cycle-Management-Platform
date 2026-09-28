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

    public const string TotalCountHeader = "X-Total-Count";

    /// <summary>
    /// Returns one page of the tenant's stalled engagements, most urgent first (CSTD-34).
    /// Owner/Staff only. The body stays a plain array (empty when nothing is stalled, never 404);
    /// the total before paging is in the X-Total-Count header.
    /// </summary>
    /// <param name="mine">Only engagements where the caller is the responsible staff member.</param>
    /// <param name="stage">Only engagements in this stage (e.g. DocumentCollection).</param>
    /// <param name="minOverdueHours">Only engagements at least this many hours overdue.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Default 25, max 100.</param>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StallQueueItemDto>>> GetQueue(
        [FromQuery] string? tenantId,
        [FromQuery] bool mine = false,
        [FromQuery] string? stage = null,
        [FromQuery] int? minOverdueHours = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = StallQueueQuery.DefaultPageSize)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
            return BadRequest(new { message = "Tenant identification is required." });

        var callerStaffId = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value;
        if (mine && string.IsNullOrWhiteSpace(callerStaffId))
            return BadRequest(new { message = "mine=true needs a signed-in staff member." });

        var result = await _queueService.GetQueueAsync(
            effectiveTenantId,
            new StallQueueQuery(mine, callerStaffId, stage, minOverdueHours, page, pageSize));

        if (Response != null)
        {
            Response.Headers[TotalCountHeader] = result.TotalCount.ToString();
        }

        return Ok(result.Items);
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