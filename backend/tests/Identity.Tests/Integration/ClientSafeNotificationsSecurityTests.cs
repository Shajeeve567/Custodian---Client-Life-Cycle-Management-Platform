using System.Security.Claims;
using Custodian.Identity.Contracts;
using Custodian.Identity.Controllers;
using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests.Integration;

public class ClientSafeNotificationsSecurityTests
{
    private static (IdentityDbContext db, INotificationRepository repo, TenantContext tenantContext) CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantContext = new TenantContext { TenantId = tenantId.ToString() };
        var db = new IdentityDbContext(options, tenantContext);
        var repo = new NotificationRepository(db);

        return (db, repo, tenantContext);
    }

    private static NotificationsController CreateController(
        INotificationRepository repo,
        TenantContext tenantContext,
        Guid clientId,
        Guid tenantId,
        string role = "Client",
        Guid? subClaim = null)
    {
        var controller = new NotificationsController(repo, tenantContext);
        var claims = new List<Claim>
        {
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("client_id", clientId.ToString()),
            new Claim(ClaimTypes.Role, role)
        };

        if (subClaim.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subClaim.Value.ToString()));
        }
        else
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, clientId.ToString()));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"))
            }
        };

        return controller;
    }

    [Fact]
    public async Task SameTenant_ClientA2_CannotRead_ClientA_Notifications()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientA2 = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var notificationA = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Confidential Client A message",
            SourceEventType = "document.verified",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repo.CreateAsync(notificationA);

        var controllerA2 = CreateController(repo, tenantContext, clientId: clientA2, tenantId: tenantId);

        // Act - Client A2 tries to query Client A's notifications with ?clientId=<ClientA>
        var result = await controllerA2.GetNotifications(clientA);

        // Assert - Rejected with 403 Forbidden
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task SameTenant_ClientA2_CannotGet_ClientA_UnreadCount()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientA2 = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var notificationA = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Secret",
            SourceEventType = "test",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repo.CreateAsync(notificationA);

        var controllerA2 = CreateController(repo, tenantContext, clientId: clientA2, tenantId: tenantId);

        // Act - Client A2 queries Client A's unread count
        var result = await controllerA2.GetUnreadCount(clientA);

        // Assert - Rejected with 403 Forbidden
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task SameTenant_ClientA2_CannotMarkAllRead_ForClientA_AndUnreadStatePreserved()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientA2 = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var notificationA = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Important action required",
            SourceEventType = "requirement.requested",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repo.CreateAsync(notificationA);

        var controllerA2 = CreateController(repo, tenantContext, clientId: clientA2, tenantId: tenantId);

        // Act - Client A2 attempts to bulk mark read for Client A
        var result = await controllerA2.MarkAllAsRead(clientA);

        // Assert - 403 Forbidden
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);

        // Verify Client A's notification remains UNREAD in database
        var unreadCountA = await repo.GetUnreadCountAsync(clientA, tenantId);
        Assert.Equal(1, unreadCountA);

        var savedNotification = await repo.GetByIdAsync(notificationA.NotificationId, tenantId);
        Assert.NotNull(savedNotification);
        Assert.False(savedNotification.IsRead);
    }

    [Fact]
    public async Task SameTenant_ClientA2_CannotMarkAsRead_ClientA_Notification_Returns404_AndPreservesUnreadState()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientA2 = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var notificationA = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Client A private task",
            SourceEventType = "condition.attached",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repo.CreateAsync(notificationA);

        var controllerA2 = CreateController(repo, tenantContext, clientId: clientA2, tenantId: tenantId);

        // Act - Client A2 tries to mark Client A's notification ID as read
        var result = await controllerA2.MarkAsRead(notificationA.NotificationId);

        // Assert - Returns 404 Not Found to prevent enumeration
        Assert.IsType<NotFoundObjectResult>(result.Result);

        // Verify Client A's notification is STILL UNREAD
        var savedNotification = await repo.GetByIdAsync(notificationA.NotificationId, tenantId);
        Assert.NotNull(savedNotification);
        Assert.False(savedNotification.IsRead);
    }

    [Fact]
    public async Task InconsistentIdentityClaims_SubDiffersFromClientId_Returns403Forbidden()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var conflictingSub = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var controller = CreateController(repo, tenantContext, clientId: clientId, tenantId: tenantId, subClaim: conflictingSub);

        // Act
        var result = await controller.GetNotifications(null);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task CrossTenantMismatch_ClaimTenantDiffersFromContextTenant_Returns403Forbidden()
    {
        // Arrange
        var claimTenantId = Guid.NewGuid();
        var contextTenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, _) = CreateContext(claimTenantId);

        // Context reflects a different tenant ID
        var mismatchedTenantContext = new TenantContext { TenantId = contextTenantId.ToString() };
        var controller = CreateController(repo, mismatchedTenantContext, clientId: clientId, tenantId: claimTenantId);

        // Act
        var result = await controller.GetNotifications(null);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task MatchingOrOmittedClientId_SucceedsForAuthorizedClient()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var notification = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Valid notification",
            SourceEventType = "document.verified",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repo.CreateAsync(notification);

        var controller = CreateController(repo, tenantContext, clientId: clientId, tenantId: tenantId);

        // Act 1: Omitted clientId
        var resultOmitted = await controller.GetNotifications(null);
        var okOmitted = Assert.IsType<OkObjectResult>(resultOmitted.Result);
        var listOmitted = Assert.IsType<List<NotificationResponse>>(okOmitted.Value);
        Assert.Single(listOmitted);

        // Act 2: Explicit matching clientId
        var resultMatching = await controller.GetNotifications(clientId);
        var okMatching = Assert.IsType<OkObjectResult>(resultMatching.Result);
        var listMatching = Assert.IsType<List<NotificationResponse>>(okMatching.Value);
        Assert.Single(listMatching);
    }

    [Fact]
    public async Task MissingSubClaim_Returns403Forbidden()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var controller = new NotificationsController(repo, tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim("client_id", clientId.ToString()),
                    new Claim(ClaimTypes.Role, "Client")
                    // Missing sub / NameIdentifier
                }, "TestAuth"))
            }
        };

        // Act
        var result = await controller.GetNotifications(null);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task MalformedSubClaim_Returns403Forbidden()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var controller = new NotificationsController(repo, tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenant_id", tenantId.ToString()),
                    new Claim("client_id", clientId.ToString()),
                    new Claim(ClaimTypes.Role, "Client"),
                    new Claim(ClaimTypes.NameIdentifier, "not-a-valid-guid")
                }, "TestAuth"))
            }
        };

        // Act
        var result = await controller.GetNotifications(null);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }

    [Fact]
    public async Task OnlyTenantIdCamelCase_WithoutTenant_Id_Returns403Forbidden()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var controller = new NotificationsController(repo, tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("tenantId", tenantId.ToString()), // camelCase only
                    new Claim("client_id", clientId.ToString()),
                    new Claim(ClaimTypes.Role, "Client"),
                    new Claim(ClaimTypes.NameIdentifier, clientId.ToString())
                }, "TestAuth"))
            }
        };

        // Act
        var result = await controller.GetNotifications(null);

        // Assert
        var forbiddenResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbiddenResult.StatusCode);
    }
}
