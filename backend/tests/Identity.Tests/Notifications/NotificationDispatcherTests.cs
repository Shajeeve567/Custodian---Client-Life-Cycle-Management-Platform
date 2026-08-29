using Custodian.Identity.Services.Notifications;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Identity.Tests.Notifications;

public class NotificationDispatcherTests
{
    private readonly Mock<INotificationDeliveryStrategy> _inAppStrategyMock;
    private readonly Mock<INotificationDeliveryStrategy> _emailStrategyMock;
    private readonly Mock<IEventDeduplicator> _deduplicatorMock;
    private readonly Mock<ILogger<ClientNotificationDispatcher>> _loggerMock;
    private readonly ClientNotificationDispatcher _dispatcher;

    public NotificationDispatcherTests()
    {
        _inAppStrategyMock = new Mock<INotificationDeliveryStrategy>();
        _inAppStrategyMock.Setup(s => s.Channel).Returns(NotificationChannel.InAppPortal);
        _inAppStrategyMock.Setup(s => s.CanHandleAsync(It.IsAny<NotificationContext>())).ReturnsAsync(true);
        _inAppStrategyMock.Setup(s => s.DeliverAsync(It.IsAny<NotificationContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _emailStrategyMock = new Mock<INotificationDeliveryStrategy>();
        _emailStrategyMock.Setup(s => s.Channel).Returns(NotificationChannel.Email);
        _emailStrategyMock.Setup(s => s.CanHandleAsync(It.IsAny<NotificationContext>())).ReturnsAsync(true);
        _emailStrategyMock.Setup(s => s.DeliverAsync(It.IsAny<NotificationContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _deduplicatorMock = new Mock<IEventDeduplicator>();
        _deduplicatorMock.Setup(d => d.IsDuplicate(It.IsAny<string>())).Returns(false);

        _loggerMock = new Mock<ILogger<ClientNotificationDispatcher>>();

        _dispatcher = new ClientNotificationDispatcher(
            new[] { _inAppStrategyMock.Object, _emailStrategyMock.Object },
            _deduplicatorMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task DispatchAsync_ShouldInvokeAllMatchingStrategies()
    {
        // Arrange
        var context = new NotificationContext
        {
            EventId = "evt-123",
            TenantId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Message = "Document verified",
            Channels = new[] { NotificationChannel.InAppPortal, NotificationChannel.Email }
        };

        // Act
        await _dispatcher.DispatchAsync(context);

        // Assert
        _inAppStrategyMock.Verify(s => s.DeliverAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        _emailStrategyMock.Verify(s => s.DeliverAsync(context, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_ShouldSkipDelivery_WhenEventIsDuplicate()
    {
        // Arrange
        _deduplicatorMock.Setup(d => d.IsDuplicate("duplicate-evt")).Returns(true);

        var context = new NotificationContext
        {
            EventId = "duplicate-evt",
            TenantId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Message = "Duplicate event"
        };

        // Act
        await _dispatcher.DispatchAsync(context);

        // Assert: Neither strategy should be called
        _inAppStrategyMock.Verify(s => s.DeliverAsync(It.IsAny<NotificationContext>(), It.IsAny<CancellationToken>()), Times.Never);
        _emailStrategyMock.Verify(s => s.DeliverAsync(It.IsAny<NotificationContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_ShouldOnlyInvokeEnabledChannels()
    {
        // Arrange: Only InAppPortal requested
        var context = new NotificationContext
        {
            EventId = "evt-inapp-only",
            TenantId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Message = "In-app only message",
            Channels = new[] { NotificationChannel.InAppPortal }
        };

        // Act
        await _dispatcher.DispatchAsync(context);

        // Assert
        _inAppStrategyMock.Verify(s => s.DeliverAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        _emailStrategyMock.Verify(s => s.DeliverAsync(It.IsAny<NotificationContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DispatchAsync_ShouldNotFailOtherStrategies_WhenOneStrategyThrowsException()
    {
        // Arrange: Email throws an exception, InApp should still succeed
        _emailStrategyMock.Setup(s => s.DeliverAsync(It.IsAny<NotificationContext>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Resend API unreachable"));

        var context = new NotificationContext
        {
            EventId = "evt-error-isolation",
            TenantId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            Message = "Resilience test"
        };

        // Act
        await _dispatcher.DispatchAsync(context);

        // Assert
        _inAppStrategyMock.Verify(s => s.DeliverAsync(context, It.IsAny<CancellationToken>()), Times.Once);
        _emailStrategyMock.Verify(s => s.DeliverAsync(context, It.IsAny<CancellationToken>()), Times.Once);
    }
}
