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

        var requirements = await _requirementService.GetRequirementsByEngagementAsync(engagementId, effectiveTenantId, clientView);
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

        dto.SubmittedByActor ??= ResolveActor();

        var result = await _requirementService.SubmitRequirementAsync(engagementId, requirementId, effectiveTenantId, dto);
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
