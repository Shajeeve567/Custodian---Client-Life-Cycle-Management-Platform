using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize]
[ApiController]
[Route("api/engagements/{engagementId:guid}/conditions")]
public class EngagementConditionsController : ControllerBase
{
    private readonly IConditionService _conditionService;
    private readonly IClientActionService _clientActionService;
    private readonly ILogger<EngagementConditionsController> _logger;

    public EngagementConditionsController(
        IConditionService conditionService,
        IClientActionService clientActionService,
        ILogger<EngagementConditionsController> logger)
    {
        _conditionService = conditionService;
        _clientActionService = clientActionService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/engagements/{engagementId}/conditions
    /// Staff attaches an Approval or Payment condition to an engagement.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ConditionResponseDto>> AttachCondition(
        [FromRoute] Guid engagementId,
        [FromBody] AttachConditionDto dto,
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

        var forbiddenResult = EnsureStaffOrOwner();
        if (forbiddenResult != null)
        {
            return forbiddenResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actor = ResolveActor() ?? "staff-user";

        try
        {
            var result = await _conditionService.AttachConditionAsync(engagementId, effectiveTenantId, dto, actor);
            return CreatedAtAction(nameof(GetConditionById), new { engagementId, conditionId = result.ConditionId, tenantId = effectiveTenantId }, result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/engagements/{engagementId}/conditions
    /// Lists conditions for an engagement. Staff gets full view (optional ?includeInactive=true).
    /// Client gets active-only client-safe view, scoped to engagements they own.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetConditions(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId,
        [FromQuery] bool? includeInactive)
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

        bool isClient = IsClientCaller();
        bool isStaffOrOwner = IsStaffOrOwnerCaller();

        if (isClient)
        {
            var callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId))
            {
                return Forbid();
            }

            var ownsEngagement = await _clientActionService.ClientOwnsEngagementAsync(engagementId, effectiveTenantId, callerClientId);
            if (!ownsEngagement)
            {
                return Forbid();
            }

            var clientConditions = await _conditionService.GetConditionsClientAsync(engagementId, effectiveTenantId, callerClientId);
            return Ok(clientConditions);
        }

        if (isStaffOrOwner)
        {
            var staffConditions = await _conditionService.GetConditionsStaffAsync(engagementId, effectiveTenantId, includeInactive ?? true);
            return Ok(staffConditions);
        }

        return Forbid();
    }

    /// <summary>
    /// GET /api/engagements/{engagementId}/conditions/{conditionId}
    /// Returns a single condition.
    /// </summary>
    [HttpGet("{conditionId:guid}")]
    public async Task<IActionResult> GetConditionById(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid conditionId,
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

        bool isClient = IsClientCaller();
        bool isStaffOrOwner = IsStaffOrOwnerCaller();

        if (isClient)
        {
            var callerClientId = ResolveCallerClientId();
            if (string.IsNullOrWhiteSpace(callerClientId))
            {
                return Forbid();
            }

            var ownsEngagement = await _clientActionService.ClientOwnsEngagementAsync(engagementId, effectiveTenantId, callerClientId);
            if (!ownsEngagement)
            {
                return Forbid();
            }

            var clientCondition = await _conditionService.GetConditionByIdClientAsync(engagementId, conditionId, effectiveTenantId, callerClientId);
            if (clientCondition == null)
            {
                return NotFound(new { message = $"Condition '{conditionId}' was not found." });
            }

            return Ok(clientCondition);
        }

        if (isStaffOrOwner)
        {
            var staffCondition = await _conditionService.GetConditionByIdStaffAsync(engagementId, conditionId, effectiveTenantId);
            if (staffCondition == null)
            {
                return NotFound(new { message = $"Condition '{conditionId}' was not found." });
            }

            return Ok(staffCondition);
        }

        return Forbid();
    }

    /// <summary>
    /// PATCH /api/engagements/{engagementId}/conditions/{conditionId}
    /// Updates configurable fields while active and pending.
    /// </summary>
    [HttpPatch("{conditionId:guid}")]
    public async Task<ActionResult<ConditionResponseDto>> UpdateCondition(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid conditionId,
        [FromBody] UpdateConditionDto dto,
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

        var forbiddenResult = EnsureStaffOrOwner();
        if (forbiddenResult != null)
        {
            return forbiddenResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actor = ResolveActor() ?? "staff-user";

        try
        {
            var result = await _conditionService.UpdateConditionAsync(engagementId, conditionId, effectiveTenantId, dto, actor);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// PUT /api/engagements/{engagementId}/conditions/{conditionId}/deactivate
    /// Deactivates an active condition and cancels linked client actions (idempotent).
    /// </summary>
    [HttpPut("{conditionId:guid}/deactivate")]
    public async Task<ActionResult<ConditionResponseDto>> DeactivateCondition(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid conditionId,
        [FromBody] DeactivateConditionDto dto,
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

        var forbiddenResult = EnsureStaffOrOwner();
        if (forbiddenResult != null)
        {
            return forbiddenResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var actor = ResolveActor() ?? "staff-user";

        try
        {
            var result = await _conditionService.DeactivateConditionAsync(engagementId, conditionId, effectiveTenantId, dto, actor);
            return Ok(result);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private ActionResult? EnsureStaffOrOwner()
    {
        if (IsStaffOrOwnerCaller())
        {
            return null;
        }
        return Forbid();
    }

    private bool IsClientCaller()
    {
        if (User.IsInRole("Client")) return true;
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-User-Role", out var roleHeader))
        {
            return roleHeader.ToString().Equals("Client", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private bool IsStaffOrOwnerCaller()
    {
        if (User.IsInRole("Owner") || User.IsInRole("Staff")) return true;
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-User-Role", out var roleHeader))
        {
            var role = roleHeader.ToString();
            return role.Equals("Owner", StringComparison.OrdinalIgnoreCase) || role.Equals("Staff", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

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
