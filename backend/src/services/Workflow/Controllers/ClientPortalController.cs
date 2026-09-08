using System.Security.Claims;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[ApiController]
[Route("api/portal")]
public class ClientPortalController : ControllerBase
{
    private readonly IClientPortalService _portalService;
    private readonly ILogger<ClientPortalController> _logger;

    public ClientPortalController(
        IClientPortalService portalService,
        ILogger<ClientPortalController> logger)
    {
        _portalService = portalService;
        _logger = logger;
    }

    /// <summary>
    /// Automatically resolves the active engagement for the authenticated client.
    /// Eliminates IDOR risks by deriving identity directly from the security context.
    /// </summary>
    [HttpGet("my-engagement")]
    public async Task<ActionResult<ClientPortalDashboardDto>> GetMyActiveEngagement(
        [FromQuery] string? tenantId,
        [FromQuery] string? clientId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
        }

        var effectiveClientId = ResolveClientId(clientId);
        if (string.IsNullOrWhiteSpace(effectiveClientId))
        {
            return BadRequest(new { message = "Client identification is required via JWT sub/clientId claim, X-Client-ID header, or clientId parameter." });
        }

        var dashboard = await _portalService.GetActiveDashboardForClientAsync(effectiveTenantId, effectiveClientId);
        if (dashboard == null)
        {
            return NotFound(new { message = $"No active onboarding engagement found for client '{effectiveClientId}' in tenant '{effectiveTenantId}'." });
        }

        return Ok(dashboard);
    }

    /// <summary>
    /// Retrieves the client-safe dashboard for a specific engagement GUID.
    /// Strictly verifies client ownership to prevent Insecure Direct Object References (IDOR).
    /// </summary>
    [HttpGet("engagements/{engagementId:guid}")]
    public async Task<ActionResult<ClientPortalDashboardDto>> GetEngagementDashboard(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId,
        [FromQuery] string? clientId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
        }

        // Determine if client authorization rule applies
        var effectiveClientId = ResolveClientId(clientId);
        var isClientCaller = User.IsInRole("Client") || !string.IsNullOrWhiteSpace(effectiveClientId);

        var dashboard = await _portalService.GetDashboardForEngagementAsync(
            engagementId,
            effectiveTenantId,
            isClientCaller ? effectiveClientId : null);

        if (dashboard == null)
        {
            // If the engagement exists but doesn't match this client, verify if it was an ownership violation
            if (isClientCaller && !string.IsNullOrWhiteSpace(effectiveClientId))
            {
                var unconstrainedCheck = await _portalService.GetDashboardForEngagementAsync(engagementId, effectiveTenantId, null);
                if (unconstrainedCheck != null)
                {
                    _logger.LogWarning("Security: Client {ClientId} attempted unauthorized access to engagement {EngagementId}", effectiveClientId, engagementId);
                    return StatusCode(403, new { message = "Access denied: Clients can only access their own engagement." });
                }
            }

            return NotFound(new { message = $"Engagement '{engagementId}' was not found in tenant '{effectiveTenantId}'." });
        }

        return Ok(dashboard);
    }

    private string? ResolveTenantId(string? queryTenantId)
    {
        // 1. Check HTTP header X-Tenant-ID
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            var headerTenant = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerTenant))
            {
                return headerTenant.Trim();
            }
        }

        // 2. Check JWT Claims
        var jwtClaimTenant = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaimTenant))
        {
            return jwtClaimTenant.Trim();
        }

        // 3. Fallback to Query String Parameter
        if (!string.IsNullOrWhiteSpace(queryTenantId))
        {
            return queryTenantId.Trim();
        }

        return null;
    }

    private string? ResolveClientId(string? queryClientId)
    {
        // 1. Check HTTP header X-Client-ID
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Client-ID", out var headerValue))
        {
            var headerClient = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerClient))
            {
                return headerClient.Trim();
            }
        }

        // 2. Check JWT Claims: client_id, clientId, sub, or NameIdentifier
        var jwtClaimClient = User?.FindFirst("client_id")?.Value
            ?? User?.FindFirst("clientId")?.Value
            ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtClaimClient))
        {
            return jwtClaimClient.Trim();
        }

        // 3. Fallback to Query String Parameter
        if (!string.IsNullOrWhiteSpace(queryClientId))
        {
            return queryClientId.Trim();
        }

        return null;
    }
}
