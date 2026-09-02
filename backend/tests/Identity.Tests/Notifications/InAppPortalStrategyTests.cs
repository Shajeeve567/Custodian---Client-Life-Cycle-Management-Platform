using Custodian.Identity.Domain;
using Custodian.Identity.Services.Notifications;
using Custodian.Identity.Services.Notifications.Strategies;
using Identity.Data;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Identity.Tests.Notifications;

public class InAppPortalStrategyTests
{
    private readonly Mock<INotificationRepository> _repositoryMock;
    private readonly Mock<ILogger<InAppPortalNotificationStrategy>> _loggerMock;
    private readonly InAppPortalNotificationStrategy _strategy;

    public InAppPortalStrategyTests()
    {
        _repositoryMock = new Mock<INotificationRepository>();
        _loggerMock = new Mock<ILogger<InAppPortalNotificationStrategy>>();
        _strategy = new InAppPortalNotificationStrategy(_repositoryMock.Object, _loggerMock.Object);
    }

    [Fact]
    public void Channel_ShouldBeInAppPortal()
    {
        Assert.Equal(NotificationChannel.InAppPortal, _strategy.Channel);
    }

    [Fact]
    public async Task CanHandleAsync_ShouldReturnTrue_WhenTenantAndClientValid()
    {
        var context = new NotificationContext
        {
            TenantId = Guid.NewGuid(),
            ClientId = Guid.NewGuid()
        };

        var result = await _strategy.CanHandleAsync(context);
        Assert.True(result);
    }

    [Fact]
    public async Task CanHandleAsync_ShouldReturnFalse_WhenTenantOrClientEmpty()
    {
        var contextEmptyTenant = new NotificationContext
        {
            TenantId = Guid.Empty,
            ClientId = Guid.NewGuid()
        };

        var contextEmptyClient = new NotificationContext
        {
            TenantId = Guid.NewGuid(),
            ClientId = Guid.Empty
        };

        Assert.False(await _strategy.CanHandleAsync(contextEmptyTenant));
        Assert.False(await _strategy.CanHandleAsync(contextEmptyClient));
    }

    [Fact]
    public async Task DeliverAsync_ShouldPersistNotificationWithIsReadFalse()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var context = new NotificationContext
        {
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Your engagement has started.",
            SourceEventType = "engagement.started"
        };

        Notification? capturedNotification = null;
        _repositoryMock.Setup(r => r.CreateAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()))
            .Callback<Notification, CancellationToken>((n, _) => capturedNotification = n)
            .ReturnsAsync((Notification n, CancellationToken _) => n);

        // Act
        var result = await _strategy.DeliverAsync(context);

        // Assert
        Assert.True(result);
        _repositoryMock.Verify(r => r.CreateAsync(It.IsAny<Notification>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(capturedNotification);
        Assert.Equal(tenantId, capturedNotification.TenantId);
        Assert.Equal(clientId, capturedNotification.ClientId);
        Assert.Equal("Your engagement has started.", capturedNotification.Message);
        Assert.Equal("engagement.started", capturedNotification.SourceEventType);
        Assert.False(capturedNotification.IsRead);
    }
}
