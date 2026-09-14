using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

/// <summary>
/// CSTD-16 (Requirements Collection). Tenant-resolution and view-selection logic here
/// intentionally mirrors ClientActionsController's (copied, not shared) — extracting a common
/// helper was flagged during planning as a good follow-up, but EngagementsController's simpler
/// variant lacks X-Tenant-ID header support, so a shared extraction would change that
/// controller's existing, already-tested behavior. Out of scope for this story.
/// </summary>
[Authorize]
[ApiController]
[Route("api/engagements/{engagementId:guid}/requirements")]
public class RequirementsController : ControllerBase
{
    private readonly IRequirementService _requirementService;
    private readonly ILogger<RequirementsController> _logger;

    public RequirementsController(IRequirementService requirementService, ILogger<RequirementsController> logger)
    {
        _requirementService = requirementService;
        _logger = logger;
    }

    /// <summary>
    /// Lists requirements for an engagement. Same role-based view resolution as
    /// ClientActionsController.GetActionHistory (CSTD-12): a Client-role caller always gets
    /// the client-safe view; anyone else requesting the staff view must actually hold the
    /// Owner or Staff role.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RequirementResponseDto>>> GetRequirements(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId,
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
            clientView = true;
        }

        // IDOR protection (CSTD-22): a Client-role caller is constrained to engagements they
        // actually own; fail closed if we can't resolve who they are rather than silently
        // letting the query through unconstrained.
        string? callerClientId = null;
        if (isClient)
        {
            callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId))
            {
                return Forbid();
            }
        }

        var requirements = await _requirementService.GetRequirementsByEngagementAsync(engagementId, effectiveTenantId, clientView, callerClientId);
        return Ok(requirements);
    }

    /// <summary>
    /// Staff requests a new piece of required client information.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<RequirementResponseDto>> RequestRequirement(
        [FromRoute] Guid engagementId,
        [FromBody] RequestRequirementDto dto,
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

        dto.RequestedByActor ??= ResolveActor();

        try
        {
            var result = await _requirementService.RequestRequirementAsync(engagementId, effectiveTenantId, dto);
            return CreatedAtAction(nameof(GetRequirements), new { engagementId, tenantId = effectiveTenantId }, result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Failed to request requirement for engagement {EngagementId}", engagementId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Client submits the requested value.
    /// </summary>
    [HttpPut("{requirementId:guid}/submit")]
    public async Task<ActionResult<RequirementResponseDto>> SubmitRequirement(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid requirementId,
        [FromBody] SubmitRequirementDto dto,
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

        // IDOR protection (CSTD-22): see GetRequirements for the same fail-closed rationale.
        string? callerClientId = null;
        if (User.IsInRole("Client"))
        {
            callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId))
            {
                return Forbid();
            }
        }

        dto.SubmittedByActor ??= ResolveActor();

        var result = await _requirementService.SubmitRequirementAsync(engagementId, requirementId, effectiveTenantId, dto, callerClientId);
        if (result == null)
        {
            return NotFound(new { message = $"Requirement '{requirementId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
        }

        return Ok(result);
    }

    /// <summary>
    /// Staff approves or rejects a submitted requirement. Restricted to authorized staff.
    /// </summary>
    [HttpPut("{requirementId:guid}/review")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<RequirementResponseDto>> ReviewRequirement(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid requirementId,
        [FromBody] ReviewRequirementDto dto,
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

        dto.ReviewerActor ??= ResolveActor() ?? string.Empty;

        try
        {
            var result = await _requirementService.ReviewRequirementAsync(engagementId, requirementId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Requirement '{requirementId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private ActionResult? CheckStaffAuthorization()
    {
        if (User?.Identity?.IsAuthenticated == true)
        {
            var isStaff = User.IsInRole("Owner") || User.IsInRole("Staff") ||
                          User.Claims.Any(c => c.Type == System.Security.Claims.ClaimTypes.Role &&
                              (c.Value.Equals("Owner", StringComparison.OrdinalIgnoreCase) || c.Value.Equals("Staff", StringComparison.OrdinalIgnoreCase)));

            return isStaff ? null : Forbid();
        }

        if (Request?.Headers != null && Request.Headers.TryGetValue("X-User-Role", out var roleHeader))
        {
            var role = roleHeader.ToString();
            var isStaff = role.Equals("Owner", StringComparison.OrdinalIgnoreCase) || role.Equals("Staff", StringComparison.OrdinalIgnoreCase);
            return isStaff ? null : Forbid();
        }

        return null;
    }

    /// <summary>
    /// Resolves the calling Client's own client id from JWT claims — never trusts a query
    /// param/header for this, unlike tenant resolution, since there is no legitimate
    /// "client acting on someone else's behalf" case here (mirrors the identity-resolution
    /// half of ClientPortalController.ResolveClientId, without that method's staff-preview
    /// override paths, which don't apply to a Client-role caller).
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

    private string? ResolveActor()
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

            if (!string.IsNullOrWhiteSpace(queryTenantId) &&
                !string.Equals(queryTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

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

        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
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
