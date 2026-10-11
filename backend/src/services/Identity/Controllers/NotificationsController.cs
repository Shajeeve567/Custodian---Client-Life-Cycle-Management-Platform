using System.Security.Claims;
using Custodian.Identity.Contracts;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Identity.Controllers;

[Authorize(Roles = "Client")]
[ApiController]
[Route("api/[controller]")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationRepository _notificationRepository;
    private readonly TenantContext _tenantContext;

    public NotificationsController(
        INotificationRepository notificationRepository,
        TenantContext tenantContext)
    {
        _notificationRepository = notificationRepository;
        _tenantContext = tenantContext;
    }

    [HttpGet]
    public async Task<ActionResult<List<NotificationResponse>>> GetNotifications(
        [FromQuery] Guid? clientId,
        [FromQuery] bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCallerIdentity(out var tenantId, out var authenticatedClientId, out var authError))
        {
            return authError!;
        }

        if (!ValidateExplicitClientId(clientId, authenticatedClientId, out var conflictError))
        {
            return conflictError!;
        }

        var notifications = await _notificationRepository.GetByClientAsync(
            authenticatedClientId,
            tenantId,
            unreadOnly,
            cancellationToken);

        var response = notifications.Select(n => new NotificationResponse(
            n.NotificationId,
            n.TenantId,
            n.ClientId,
            n.Message,
            n.SourceEventType,
            n.IsRead,
            n.CreatedAt
        )).ToList();

        return Ok(response);
    }

    [HttpGet("unread-count")]
    public async Task<ActionResult<UnreadCountResponse>> GetUnreadCount(
        [FromQuery] Guid? clientId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCallerIdentity(out var tenantId, out var authenticatedClientId, out var authError))
        {
            return authError!;
        }

        if (!ValidateExplicitClientId(clientId, authenticatedClientId, out var conflictError))
        {
            return conflictError!;
        }

        var count = await _notificationRepository.GetUnreadCountAsync(
            authenticatedClientId,
            tenantId,
            cancellationToken);

        return Ok(new UnreadCountResponse(count));
    }

    [HttpPatch("{id:guid}/read")]
    public async Task<ActionResult<MarkAsReadResponse>> MarkAsRead(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCallerIdentity(out var tenantId, out var authenticatedClientId, out var authError))
        {
            return authError!;
        }

        var success = await _notificationRepository.MarkAsReadAsync(
            id,
            tenantId,
            clientId: authenticatedClientId,
            ct: cancellationToken);

        if (!success)
        {
            return NotFound(new { error = $"Notification with ID {id} not found." });
        }

        return Ok(new MarkAsReadResponse(id, true, DateTimeOffset.UtcNow));
    }

    [HttpPatch("read-all")]
    public async Task<ActionResult<MarkAllAsReadResponse>> MarkAllAsRead(
        [FromQuery] Guid? clientId,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCallerIdentity(out var tenantId, out var authenticatedClientId, out var authError))
        {
            return authError!;
        }

        if (!ValidateExplicitClientId(clientId, authenticatedClientId, out var conflictError))
        {
            return conflictError!;
        }

        var updatedCount = await _notificationRepository.MarkAllAsReadAsync(
            authenticatedClientId,
            tenantId,
            cancellationToken);

        return Ok(new MarkAllAsReadResponse(updatedCount, DateTimeOffset.UtcNow));
    }

    private bool TryGetCallerIdentity(out Guid tenantId, out Guid clientId, out ActionResult? failureResult)
    {
        tenantId = Guid.Empty;
        clientId = Guid.Empty;
        failureResult = null;

        // 1. Enforce Client role
        if (!User.IsInRole("Client"))
        {
            failureResult = StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden. Only Client role can access notifications." });
            return false;
        }

        // 2. Require valid, non-empty tenant_id claim in JWT (never rely on header-only tenant context)
        var tenantClaim = User.FindFirst("tenant_id")?.Value;
        if (string.IsNullOrWhiteSpace(tenantClaim) || !Guid.TryParse(tenantClaim, out tenantId) || tenantId == Guid.Empty)
        {
            failureResult = StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden. A valid workspace tenant_id claim is required." });
            return false;
        }

        // Ensure tenant context matches claim if tenant context was established
        if (!string.IsNullOrWhiteSpace(_tenantContext.TenantId) &&
            Guid.TryParse(_tenantContext.TenantId, out var contextTenantId) &&
            contextTenantId != tenantId)
        {
            failureResult = StatusCode(StatusCodes.Status403Forbidden, new { error = "Cross-tenant access forbidden." });
            return false;
        }

        // 3. Require valid, non-empty client_id claim in JWT
        var clientClaim = User.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(clientClaim) || !Guid.TryParse(clientClaim, out clientId) || clientId == Guid.Empty)
        {
            failureResult = StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden. A valid client_id claim is required." });
            return false;
        }

        // 4. Reject missing, malformed, or inconsistent subject identity claims
        var subClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subClaim) || !Guid.TryParse(subClaim, out var subGuid) || subGuid != clientId)
        {
            failureResult = StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden. Inconsistent user identity claims." });
            return false;
        }

        return true;
    }

    private bool ValidateExplicitClientId(Guid? explicitClientId, Guid authenticatedClientId, out ActionResult? conflictResult)
    {
        conflictResult = null;
        if (explicitClientId.HasValue && explicitClientId.Value != Guid.Empty && explicitClientId.Value != authenticatedClientId)
        {
            conflictResult = StatusCode(StatusCodes.Status403Forbidden, new { error = "Forbidden. Caller cannot access notifications for another client." });
            return false;
        }

        return true;
    }
}
