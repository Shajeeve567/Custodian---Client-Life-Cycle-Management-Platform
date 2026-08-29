using System.Security.Claims;
using Custodian.Identity.Contracts;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Identity.Controllers;

[Authorize]
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
        var targetClientId = ResolveClientId(clientId);
        if (targetClientId == Guid.Empty)
        {
            return BadRequest(new { error = "A valid clientId must be provided or present in user claims." });
        }

        var tenantId = GetTenantId();
        var notifications = await _notificationRepository.GetByClientAsync(targetClientId, tenantId, unreadOnly, cancellationToken);

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
        var targetClientId = ResolveClientId(clientId);
        if (targetClientId == Guid.Empty)
        {
            return BadRequest(new { error = "A valid clientId must be provided or present in user claims." });
        }

        var tenantId = GetTenantId();
        var count = await _notificationRepository.GetUnreadCountAsync(targetClientId, tenantId, cancellationToken);

        return Ok(new UnreadCountResponse(count));
    }

    [HttpPatch("{id:guid}/read")]
    public async Task<ActionResult<MarkAsReadResponse>> MarkAsRead(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var tenantId = GetTenantId();
        var success = await _notificationRepository.MarkAsReadAsync(id, tenantId, clientId: null, ct: cancellationToken);

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
        var targetClientId = ResolveClientId(clientId);
        if (targetClientId == Guid.Empty)
        {
            return BadRequest(new { error = "A valid clientId must be provided or present in user claims." });
        }

        var tenantId = GetTenantId();
        var updatedCount = await _notificationRepository.MarkAllAsReadAsync(targetClientId, tenantId, cancellationToken);

        return Ok(new MarkAllAsReadResponse(updatedCount, DateTimeOffset.UtcNow));
    }

    private Guid GetTenantId()
    {
        var rawTenant = _tenantContext.TenantId;
        return Guid.TryParse(rawTenant, out var tenantId) ? tenantId : Guid.Empty;
    }

    private Guid ResolveClientId(Guid? explicitClientId)
    {
        if (explicitClientId.HasValue && explicitClientId.Value != Guid.Empty)
        {
            return explicitClientId.Value;
        }

        // Fallback to JWT claims (sub or client_id claim)
        var claimValue = User.FindFirstValue(ClaimTypes.NameIdentifier) 
                      ?? User.FindFirstValue("sub") 
                      ?? User.FindFirstValue("client_id");

        return Guid.TryParse(claimValue, out var claimGuid) ? claimGuid : Guid.Empty;
    }
}
