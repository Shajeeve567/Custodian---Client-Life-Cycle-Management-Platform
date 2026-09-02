using System.Security.Claims;
using Custodian.Identity.Contracts;
using Custodian.Identity.Controllers;
using Custodian.Identity.Domain;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Identity.Tests.Controllers;

public class NotificationsControllerTests
{
    private readonly Mock<INotificationRepository> _repositoryMock;
    private readonly TenantContext _tenantContext;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();

    public NotificationsControllerTests()
    {
        _repositoryMock = new Mock<INotificationRepository>();
        _tenantContext = new TenantContext { TenantId = _tenantId.ToString() };
    }

    private NotificationsController CreateController(ClaimsPrincipal? user = null)
    {
        var controller = new NotificationsController(_repositoryMock.Object, _tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = user ?? new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, _clientId.ToString())
                }, "TestAuth"))
            }
        };
        return controller;
    }

    [Fact]
    public async Task GetNotifications_WithValidClientId_ShouldReturnMappedNotifications()
    {
        // Arrange
        var notifications = new List<Notification>
        {
            new()
            {
                NotificationId = Guid.NewGuid(),
                TenantId = _tenantId,
                ClientId = _clientId,
                Message = "Document verified.",
                SourceEventType = "document.verified",
                IsRead = false,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        _repositoryMock.Setup(r => r.GetByClientAsync(_clientId, _tenantId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(notifications);

        var controller = CreateController();

        // Act
        var result = await controller.GetNotifications(_clientId, unreadOnly: false);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<List<NotificationResponse>>(okResult.Value);
        Assert.Single(response);
        Assert.Equal("Document verified.", response[0].Message);
        Assert.False(response[0].IsRead);
    }

    [Fact]
    public async Task GetNotifications_WithUnreadFilter_ShouldPassFilterToRepository()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetByClientAsync(_clientId, _tenantId, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Notification>());

        var controller = CreateController();

        // Act
        var result = await controller.GetNotifications(_clientId, unreadOnly: true);

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        _repositoryMock.Verify(r => r.GetByClientAsync(_clientId, _tenantId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNotifications_WithoutClientIdAndNoClaims_ShouldReturnBadRequest()
    {
        // Arrange
        var controller = CreateController(new ClaimsPrincipal(new ClaimsIdentity())); // No claims

        // Act
        var result = await controller.GetNotifications(null);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetUnreadCount_ShouldReturnCorrectCount()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetUnreadCountAsync(_clientId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var controller = CreateController();

        // Act
        var result = await controller.GetUnreadCount(_clientId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<UnreadCountResponse>(okResult.Value);
        Assert.Equal(5, response.UnreadCount);
    }

    [Fact]
    public async Task MarkAsRead_WhenFound_ShouldReturn200Ok()
    {
        // Arrange
        var notificationId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.MarkAsReadAsync(notificationId, _tenantId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = CreateController();

        // Act
        var result = await controller.MarkAsRead(notificationId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MarkAsReadResponse>(okResult.Value);
        Assert.Equal(notificationId, response.NotificationId);
        Assert.True(response.IsRead);
    }

    [Fact]
    public async Task MarkAsRead_WhenNotFound_ShouldReturn404()
    {
        // Arrange
        var notificationId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.MarkAsReadAsync(notificationId, _tenantId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var controller = CreateController();

        // Act
        var result = await controller.MarkAsRead(notificationId);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task MarkAllAsRead_ShouldReturnUpdatedCount()
    {
        // Arrange
        _repositoryMock.Setup(r => r.MarkAllAsReadAsync(_clientId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(4);

        var controller = CreateController();

        // Act
        var result = await controller.MarkAllAsRead(_clientId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<MarkAllAsReadResponse>(okResult.Value);
        Assert.Equal(4, response.UpdatedCount);
    }
}
