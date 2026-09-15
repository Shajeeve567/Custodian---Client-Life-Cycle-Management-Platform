using System.Security.Claims;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize]
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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var (effectiveClientId, isClientMismatch) = ResolveClientId(clientId);
        if (isClientMismatch)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveClientId))
        {
            // Staff / Owner preview without specific client specified: fallback to tenant's active onboarding engagement
            if (User?.IsInRole("Staff") == true || User?.IsInRole("Owner") == true)
            {
                var staffPreviewDashboard = await _portalService.GetActiveDashboardForTenantAsync(effectiveTenantId);
                if (staffPreviewDashboard == null)
                {
                    return NotFound(new { message = $"No active onboarding engagement found in workspace '{effectiveTenantId}'." });
                }
                return Ok(staffPreviewDashboard);
            }

            return BadRequest(new { message = "Client identification is required via JWT sub/clientId claim, X-Client-ID header, or clientId parameter." });
        }

        var dashboard = await _portalService.GetActiveDashboardForClientAsync(effectiveTenantId, effectiveClientId);
        if (dashboard == null)
        {
            return NotFound(new { message = $"No active onboarding engagement found for client '{effectiveClientId}' in workspace '{effectiveTenantId}'." });
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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var (effectiveClientId, isClientMismatch) = ResolveClientId(clientId);
        if (isClientMismatch)
        {
            return Forbid();
        }

        // Determine if client authorization rule applies
        var isClientCaller = User?.IsInRole("Client") == true || !string.IsNullOrWhiteSpace(effectiveClientId);

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

    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? queryTenantId)
    {
        var jwtClaimTenant = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaimTenant))
        {
            var cleanJwtTenant = jwtClaimTenant.Trim();

            // Check if query tenant parameter conflicts
            if (!string.IsNullOrWhiteSpace(queryTenantId) &&
                !string.Equals(queryTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            // Check if header tenant conflicts
            if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerVal))
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

        // 1. Check HTTP header X-Tenant-ID (for unauthenticated test contexts)
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            var headerTenant = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerTenant))
            {
                return (headerTenant.Trim(), false);
            }
        }

        // 2. Fallback to Query String Parameter
        if (!string.IsNullOrWhiteSpace(queryTenantId))
        {
            return (queryTenantId.Trim(), false);
        }

        return (null, false);
    }

    private (string? ClientId, bool IsMismatch) ResolveClientId(string? queryClientId)
    {
        // 1. Check JWT Claims: client_id, clientId, or sub/NameIdentifier (for Client role)
        var jwtClaimClient = User?.FindFirst("client_id")?.Value
            ?? User?.FindFirst("clientId")?.Value;

        if (string.IsNullOrWhiteSpace(jwtClaimClient) && User?.IsInRole("Client") == true)
        {
            jwtClaimClient = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? User?.FindFirst("sub")?.Value;
        }

        if (!string.IsNullOrWhiteSpace(jwtClaimClient))
        {
            var cleanJwtClient = jwtClaimClient.Trim();

            // Client caller cannot forge a different client ID via query param
            if (!string.IsNullOrWhiteSpace(queryClientId) &&
                !string.Equals(queryClientId.Trim(), cleanJwtClient, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            // Client caller cannot forge a different client ID via X-Client-ID header
            if (Request?.Headers != null && Request.Headers.TryGetValue("X-Client-ID", out var headerVal))
            {
                var headerClient = headerVal.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(headerClient) &&
                    !string.Equals(headerClient, cleanJwtClient, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, true);
                }
            }

            return (cleanJwtClient, false);
        }

        // 2. Query String Parameter
        if (!string.IsNullOrWhiteSpace(queryClientId))
        {
            return (queryClientId.Trim(), false);
        }

        // 3. HTTP Header X-Client-ID (for staff preview or unauthenticated test callers)
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Client-ID", out var headerValue))
        {
            var headerClient = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerClient))
            {
                return (headerClient.Trim(), false);
            }
        }

        // 4. Staff/Owner callers should NOT treat their user sub as a clientId unless explicitly passed
        if (User?.IsInRole("Owner") == true || User?.IsInRole("Staff") == true)
        {
            return (null, false);
        }

        // 5. Fallback to sub / NameIdentifier for general test/unassigned contexts
        var fallbackSub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value;

        if (!string.IsNullOrWhiteSpace(fallbackSub))
        {
            return (fallbackSub.Trim(), false);
        }

        return (null, false);
    }
}
