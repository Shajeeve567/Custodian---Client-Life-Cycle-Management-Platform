using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Identity.Tests;

public class NotificationRepositoryTests
{
    private static IdentityDbContext CreateDbContext(Guid tenantId, string dbName)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        var tenantContext = new TenantContext { TenantId = tenantId.ToString() };

        return new IdentityDbContext(options, tenantContext);
    }

    [Fact]
    public async Task CreateAsync_ShouldPersistNotification_WithDefaultUnreadStatus()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        var notification = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Your engagement has started.",
            SourceEventType = "engagement.started",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Act
        var created = await repo.CreateAsync(notification);

        // Assert
        Assert.NotNull(created);
        Assert.Equal(notification.NotificationId, created.NotificationId);
        Assert.False(created.IsRead);
        Assert.Equal("Your engagement has started.", created.Message);
        Assert.Equal("engagement.started", created.SourceEventType);

        var saved = await db.Notifications.FindAsync(notification.NotificationId);
        Assert.NotNull(saved);
        Assert.Equal(tenantId, saved.TenantId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNotification_WhenTenantMatches()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        var notificationId = Guid.NewGuid();
        await repo.CreateAsync(new Notification
        {
            NotificationId = notificationId,
            TenantId = tenantId,
            ClientId = clientId,
            Message = "A document has been verified.",
            SourceEventType = "document.verified",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act
        var found = await repo.GetByIdAsync(notificationId, tenantId);

        // Assert
        Assert.NotNull(found);
        Assert.Equal(notificationId, found.NotificationId);
    }

    [Fact]
    public async Task GetByIdAsync_ShouldReturnNull_WhenTenantDoesNotMatch()
    {
        // Arrange
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantA, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        var notificationId = Guid.NewGuid();
        await repo.CreateAsync(new Notification
        {
            NotificationId = notificationId,
            TenantId = tenantA,
            ClientId = clientId,
            Message = "Test message",
            SourceEventType = "test.event",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act - Attempt to query under tenantB
        var found = await repo.GetByIdAsync(notificationId, tenantB);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public async Task GetByClientAsync_ShouldReturnNotificationsForSpecificClientAndTenant()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Client A Notification 1",
            SourceEventType = "type1",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Client A Notification 2",
            SourceEventType = "type2",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientB,
            Message = "Client B Notification",
            SourceEventType = "type1",
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act
        var clientANotifications = (await repo.GetByClientAsync(clientA, tenantId)).ToList();

        // Assert
        Assert.Equal(2, clientANotifications.Count);
        Assert.All(clientANotifications, n => Assert.Equal(clientA, n.ClientId));
        // Verify ordered by newest first
        Assert.Equal("Client A Notification 2", clientANotifications[0].Message);
    }

    [Fact]
    public async Task GetByClientAsync_WithUnreadOnlyTrue_ShouldFilterOutReadNotifications()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Read notification",
            SourceEventType = "type1",
            IsRead = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        });

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Unread notification",
            SourceEventType = "type2",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act
        var unread = (await repo.GetByClientAsync(clientId, tenantId, unreadOnly: true)).ToList();

        // Assert
        Assert.Single(unread);
        Assert.Equal("Unread notification", unread[0].Message);
        Assert.False(unread[0].IsRead);
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldSetIsReadToTrue()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        var notificationId = Guid.NewGuid();
        await repo.CreateAsync(new Notification
        {
            NotificationId = notificationId,
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Pending review",
            SourceEventType = "condition.attached",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act
        var result = await repo.MarkAsReadAsync(notificationId, tenantId, clientId);

        // Assert
        Assert.True(result);
        var updated = await repo.GetByIdAsync(notificationId, tenantId);
        Assert.NotNull(updated);
        Assert.True(updated.IsRead);
    }

    [Fact]
    public async Task MarkAsReadAsync_ShouldReturnFalse_WhenNotificationNotFoundOrCrossTenant()
    {
        // Arrange
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantA, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        var notificationId = Guid.NewGuid();
        await repo.CreateAsync(new Notification
        {
            NotificationId = notificationId,
            TenantId = tenantA,
            ClientId = clientId,
            Message = "Pending review",
            SourceEventType = "condition.attached",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act - Wrong tenant or wrong client
        var crossTenantResult = await repo.MarkAsReadAsync(notificationId, tenantB, clientId);
        var crossClientResult = await repo.MarkAsReadAsync(notificationId, tenantA, Guid.NewGuid());

        // Assert
        Assert.False(crossTenantResult);
        Assert.False(crossClientResult);
    }

    [Fact]
    public async Task MarkAllAsReadAsync_ShouldUpdateAllUnreadNotificationsForClient()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Message 1",
            SourceEventType = "e1",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Message 2",
            SourceEventType = "e2",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act
        var count = await repo.MarkAllAsReadAsync(clientId, tenantId);

        // Assert
        Assert.Equal(2, count);
        var unread = await repo.GetByClientAsync(clientId, tenantId, unreadOnly: true);
        Assert.Empty(unread);
    }

    [Fact]
    public async Task GetUnreadCountAsync_ShouldReturnCorrectCount()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        using var db = CreateDbContext(tenantId, Guid.NewGuid().ToString());
        var repo = new NotificationRepository(db);

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Msg 1",
            SourceEventType = "e1",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Msg 2",
            SourceEventType = "e2",
            IsRead = true,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act
        var unreadCount = await repo.GetUnreadCountAsync(clientId, tenantId);

        // Assert
        Assert.Equal(1, unreadCount);
    }
}
