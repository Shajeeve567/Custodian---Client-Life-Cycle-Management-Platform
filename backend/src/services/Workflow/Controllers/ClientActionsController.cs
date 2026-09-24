using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Custodian.Workflow.Controllers;

[Authorize]
[ApiController]
[Route("api/engagements/{engagementId:guid}/actions")]
public class ClientActionsController : ControllerBase
{
    private readonly IClientActionService _actionService;
    private readonly ILogger<ClientActionsController> _logger;

    public ClientActionsController(
        IClientActionService actionService,
        ILogger<ClientActionsController> logger)
    {
        _actionService = actionService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves pending and completed action history for an engagement.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ClientActionResponseDto>>> GetActionHistory(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId,
        [FromQuery] string? status,
        [FromQuery] bool? isClientView)
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

        // Server-side, role-based view selection (CSTD-12 security fix — Internal Field Leak).
        // View selection must never trust the caller: a Client-role caller can never escalate to
        // the staff view via ?isClientView=false or X-Client-View, and anyone else requesting the
        // staff view must actually hold the Owner or Staff role or the request is rejected outright
        // — there is no silent fallback that would let an unauthorized/roleless caller see it.
        bool isClient = User.IsInRole("Client");
        bool isStaffOrOwner = User.IsInRole("Owner") || User.IsInRole("Staff");

        bool requestedStaffView = isClientView == false;
        if (!requestedStaffView && Request?.Headers != null &&
            Request.Headers.TryGetValue("X-Client-View", out var headerVal) &&
            bool.TryParse(headerVal, out var headerRequestsClientView) && !headerRequestsClientView)
        {
            requestedStaffView = true;
        }

        bool clientView;
        if (isClient)
        {
            clientView = true;
        }
        else if (requestedStaffView)
        {
            if (!isStaffOrOwner)
            {
                return Forbid();
            }
            clientView = false;
        }
        else
        {
            // Safe default: the staff view was never explicitly requested, so don't assume it.
            clientView = true;
        }

        // IDOR protection (CSTD-22 fix, extended here): a Client-role caller is constrained to
        // engagements they actually own; fail closed if we can't resolve who they are.
        if (isClient)
        {
            var callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId) ||
                !await _actionService.ClientOwnsEngagementAsync(engagementId, effectiveTenantId, callerClientId))
            {
                return Forbid();
            }
        }

        var actions = await _actionService.GetActionsByEngagementAsync(engagementId, effectiveTenantId, clientView, status);
        return Ok(actions);
    }

    /// <summary>
    /// Creates a new action request for an engagement.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ClientActionResponseDto>> CreateAction(
        [FromRoute] Guid engagementId,
        [FromBody] CreateClientActionDto dto,
        [FromQuery] string? tenantId)
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

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (User.IsInRole("Client"))
        {
            var callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId) ||
                !await _actionService.ClientOwnsEngagementAsync(engagementId, effectiveTenantId, callerClientId))
            {
                return Forbid();
            }
        }

        try
        {
            var result = await _actionService.CreateActionAsync(engagementId, effectiveTenantId, dto);
            return CreatedAtAction(
                nameof(GetActionHistory),
                new { engagementId, tenantId = effectiveTenantId },
                result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to create action for engagement {EngagementId}", engagementId);
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Conflict creating action for engagement {EngagementId}", engagementId);
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Marks an action as completed.
    /// </summary>
    [HttpPut("{actionId:guid}/complete")]
    public async Task<ActionResult<ClientActionResponseDto>> CompleteAction(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid actionId,
        [FromBody] CompleteClientActionDto dto,
        [FromQuery] string? tenantId)
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

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (User.IsInRole("Client"))
        {
            var callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId) ||
                !await _actionService.ClientOwnsEngagementAsync(engagementId, effectiveTenantId, callerClientId))
            {
                return Forbid();
            }
        }

        try
        {
            var result = await _actionService.CompleteActionAsync(engagementId, actionId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Action '{actionId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Client evidence upload endpoint: Automatically transitions action from Pending/Rejected to Uploaded (awaiting staff review).
    /// </summary>
    [HttpPut("{actionId:guid}/upload")]
    public async Task<ActionResult<ClientActionResponseDto>> UploadEvidence(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid actionId,
        [FromBody] UploadActionEvidenceDto dto,
        [FromQuery] string? tenantId)
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

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (User.IsInRole("Client"))
        {
            var callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId) ||
                !await _actionService.ClientOwnsEngagementAsync(engagementId, effectiveTenantId, callerClientId))
            {
                return Forbid();
            }
        }

        try
        {
            var result = await _actionService.UploadEvidenceAsync(engagementId, actionId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Action '{actionId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Staff review endpoint: Marks an action as Completed (verified/accepted) or Rejected (revision required).
    /// Restricted to authorized staff (Owner, Staff).
    /// </summary>
    [HttpPut("{actionId:guid}/review")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<ClientActionResponseDto>> ReviewAction(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid actionId,
        [FromBody] ReviewActionDto dto,
        [FromQuery] string? tenantId)
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

        var authResult = CheckStaffAuthorization();
        if (authResult != null)
        {
            return authResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        dto.ReviewerActor ??= ResolveStaffActor();

        try
        {
            var result = await _actionService.ReviewActionAsync(engagementId, actionId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Action '{actionId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Updates action status based on document human verification outcome (Verified -> Completed, Rejected -> Rejected with reason).
    /// Restricted to authorized staff (Owner, Staff).
    /// </summary>
    [HttpPut("{actionId:guid}/verification")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<ClientActionResponseDto>> ApplyVerification(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid actionId,
        [FromBody] ApplyActionVerificationDto dto,
        [FromQuery] string? tenantId)
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

        var authResult = CheckStaffAuthorization();
        if (authResult != null)
        {
            return authResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        dto.VerifiedBy ??= ResolveStaffActor();

        try
        {
            var result = await _actionService.ApplyVerificationOutcomeAsync(engagementId, actionId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Action '{actionId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private ActionResult? CheckStaffAuthorization()
    {
        // 1. If ClaimsPrincipal is authenticated, check role claims
        if (User?.Identity?.IsAuthenticated == true)
        {
            var isStaff = User.IsInRole("Owner") || User.IsInRole("Staff") ||
                          User.Claims.Any(c => c.Type == System.Security.Claims.ClaimTypes.Role &&
                              (c.Value.Equals("Owner", StringComparison.OrdinalIgnoreCase) || c.Value.Equals("Staff", StringComparison.OrdinalIgnoreCase)));

            return isStaff ? null : Forbid();
        }

        // 2. Check X-User-Role header fallback for direct testing / service-to-service calls
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-User-Role", out var roleHeader))
        {
            var role = roleHeader.ToString();
            var isStaff = role.Equals("Owner", StringComparison.OrdinalIgnoreCase) || role.Equals("Staff", StringComparison.OrdinalIgnoreCase);
            return isStaff ? null : Forbid();
        }

        return null;
    }

    /// <summary>
    /// Resolves the calling Client's own client id from JWT claims (same pattern as
    /// RequirementsController.ResolveCallerClientId, CSTD-22) — never trusts a query
    /// param/header, since there is no legitimate "client acting on someone else's behalf" case.
    /// </summary>
    private string? ResolveCallerClientId()
    {
        var jwtClaimClient = User?.FindFirst("client_id")?.Value ?? User?.FindFirst("clientId")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaimClient))
        {
            return jwtClaimClient.Trim();
        }

        var fallbackSub = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value;

        return string.IsNullOrWhiteSpace(fallbackSub) ? null : fallbackSub.Trim();
    }

    private string? ResolveStaffActor()
    {
        var actor = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value
            ?? User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
            ?? User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        if (!string.IsNullOrWhiteSpace(actor))
        {
            return actor.Trim();
        }

        if (Request?.Headers != null && Request.Headers.TryGetValue("X-User-Id", out var userHeader))
        {
            return userHeader.ToString().Trim();
        }

        return null;
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
}
