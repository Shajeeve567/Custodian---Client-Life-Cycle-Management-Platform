using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Custodian.Workflow.Controllers;

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
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
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
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
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
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
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
}
