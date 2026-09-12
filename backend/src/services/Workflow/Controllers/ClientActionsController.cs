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

        // Determine if client view rule applies (via explicit parameter, header, or role claim)
        bool clientView = isClientView ?? User.IsInRole("Client");
        if (!clientView && Request?.Headers != null && Request.Headers.TryGetValue("X-Client-View", out var headerVal))
        {
            _ = bool.TryParse(headerVal, out clientView);
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

        var result = await _actionService.CompleteActionAsync(engagementId, actionId, effectiveTenantId, dto);
        if (result == null)
        {
            return NotFound(new { message = $"Action '{actionId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
        }

        return Ok(result);
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

        var result = await _actionService.UploadEvidenceAsync(engagementId, actionId, effectiveTenantId, dto);
        if (result == null)
        {
            return NotFound(new { message = $"Action '{actionId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
        }

        return Ok(result);
    }

    /// <summary>
    /// Staff review endpoint: Marks an action as Completed (verified/accepted) or Rejected (revision required).
    /// </summary>
    [HttpPut("{actionId:guid}/review")]
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

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

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
    }

    /// <summary>
    /// Updates action status based on document human verification outcome (Verified -> Completed, Rejected -> Rejected with reason).
    /// </summary>
    [HttpPut("{actionId:guid}/verification")]
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

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

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
