using System.Security.Claims;
using System.Text.Json;
using Custodian.Identity.Contracts;
using Custodian.Identity.Controllers;
using Custodian.Identity.Domain;
using Custodian.Identity.Services.Notifications;
using Custodian.Identity.Services.Notifications.Mappers;
using Custodian.Identity.Services.Notifications.Strategies;
using Custodian.Shared.Messaging;
using Custodian.Shared.Tenancy;
using Identity.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Resend;
using Xunit;

namespace Identity.Tests.Integration;

public class ClientSafeNotificationsIntegrationTests
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
        string role = "Client")
    {
        var controller = new NotificationsController(repo, tenantContext);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, clientId.ToString()),
                    new Claim(ClaimTypes.Role, role)
                }, "TestAuth"))
            }
        };
        return controller;
    }

    [Fact]
    public async Task FullLifecycle_CreateEvents_FetchNotifications_MarkAsRead_VerifyUnreadCount()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);
        var controller = CreateController(repo, tenantContext, clientId);

        // 1. Add 3 notifications for the client
        var n1 = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Welcome to onboarding!",
            SourceEventType = "engagement.started",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var n2 = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Document verified: Passport.pdf",
            SourceEventType = "document.verified",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var n3 = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Action required: Tax Form",
            SourceEventType = "requirement.requested",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await repo.CreateAsync(n1);
        await repo.CreateAsync(n2);
        await repo.CreateAsync(n3);

        // 2. Fetch notifications via Controller
        var getResult = await controller.GetNotifications(clientId);
        var okGetResult = Assert.IsType<OkObjectResult>(getResult.Result);
        var notifications = Assert.IsType<List<NotificationResponse>>(okGetResult.Value);
        Assert.Equal(3, notifications.Count);

        // 3. Verify initial unread count == 3
        var countResult = await controller.GetUnreadCount(clientId);
        var countResponse = Assert.IsType<UnreadCountResponse>(Assert.IsType<OkObjectResult>(countResult.Result).Value);
        Assert.Equal(3, countResponse.UnreadCount);

        // 4. Mark 1 notification as read
        var markReadResult = await controller.MarkAsRead(n1.NotificationId);
        var markResponse = Assert.IsType<MarkAsReadResponse>(Assert.IsType<OkObjectResult>(markReadResult.Result).Value);
        Assert.Equal(n1.NotificationId, markResponse.NotificationId);
        Assert.True(markResponse.IsRead);

        // 5. Verify unread count decreases to 2
        var countResult2 = await controller.GetUnreadCount(clientId);
        var countResponse2 = Assert.IsType<UnreadCountResponse>(Assert.IsType<OkObjectResult>(countResult2.Result).Value);
        Assert.Equal(2, countResponse2.UnreadCount);

        // 6. Bulk Mark All As Read
        var markAllResult = await controller.MarkAllAsRead(clientId);
        var markAllResponse = Assert.IsType<MarkAllAsReadResponse>(Assert.IsType<OkObjectResult>(markAllResult.Result).Value);
        Assert.Equal(2, markAllResponse.UpdatedCount);

        // 7. Verify final unread count == 0
        var finalCountResult = await controller.GetUnreadCount(clientId);
        var finalCountResponse = Assert.IsType<UnreadCountResponse>(Assert.IsType<OkObjectResult>(finalCountResult.Result).Value);
        Assert.Equal(0, finalCountResponse.UnreadCount);
    }

    [Fact]
    public async Task TenantIsolation_TenantACannotAccessTenantBNotifications()
    {
        // Arrange
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();

        var (db, repoA, contextA) = CreateContext(tenantA);

        var notificationTenantA = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantA,
            ClientId = clientA,
            Message = "Confidential Tenant A Message",
            SourceEventType = "document.verified",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repoA.CreateAsync(notificationTenantA);

        // Switch tenant context to Tenant B
        var contextB = new TenantContext { TenantId = tenantB.ToString() };
        var repoB = new NotificationRepository(new IdentityDbContext(
            new DbContextOptionsBuilder<IdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            contextB));

        var controllerB = CreateController(repoB, contextB, clientB);

        // Act - Attempt to query or mark Tenant A's notification while authenticated as Tenant B
        var markResult = await controllerB.MarkAsRead(notificationTenantA.NotificationId);
        var getResult = await controllerB.GetNotifications(clientA);

        // Assert
        Assert.IsType<NotFoundObjectResult>(markResult.Result);
        var listResponse = Assert.IsType<List<NotificationResponse>>(Assert.IsType<OkObjectResult>(getResult.Result).Value);
        Assert.Empty(listResponse);
    }

    [Fact]
    public async Task IdempotentRead_MultipleMarkAsReadCalls_AlwaysReturns200Ok()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);
        var controller = CreateController(repo, tenantContext, clientId);

        var notification = new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Message = "Test alert",
            SourceEventType = "e1",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await repo.CreateAsync(notification);

        // Act - Call 3 times consecutively
        var res1 = await controller.MarkAsRead(notification.NotificationId);
        var res2 = await controller.MarkAsRead(notification.NotificationId);
        var res3 = await controller.MarkAsRead(notification.NotificationId);

        // Assert
        Assert.IsType<OkObjectResult>(res1.Result);
        Assert.IsType<OkObjectResult>(res2.Result);
        Assert.IsType<OkObjectResult>(res3.Result);

        var updated = await repo.GetByIdAsync(notification.NotificationId, tenantId);
        Assert.NotNull(updated);
        Assert.True(updated.IsRead);
    }

    [Fact]
    public async Task BulkMarkAllAsRead_OnlyAffectsTargetClient()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);
        var controllerA = CreateController(repo, tenantContext, clientA);

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientA,
            Message = "Client A Msg 1",
            SourceEventType = "e1",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        await repo.CreateAsync(new Notification
        {
            NotificationId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientB,
            Message = "Client B Msg 1",
            SourceEventType = "e1",
            IsRead = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // Act - Client A marks all as read
        var markAllResult = await controllerA.MarkAllAsRead(clientA);
        var markAllResponse = Assert.IsType<MarkAllAsReadResponse>(Assert.IsType<OkObjectResult>(markAllResult.Result).Value);

        // Assert
        Assert.Equal(1, markAllResponse.UpdatedCount);

        // Client B's notification must remain UNREAD
        var clientBUnread = await repo.GetUnreadCountAsync(clientB, tenantId);
        Assert.Equal(1, clientBUnread);
    }

    [Fact]
    public async Task EndToEnd_EventMappingToDatabaseAndEmailDispatch()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (db, repo, tenantContext) = CreateContext(tenantId);

        var mapper = new EventToClientSafeMessageMapper();
        var inAppStrategy = new InAppPortalNotificationStrategy(repo, Mock.Of<ILogger<InAppPortalNotificationStrategy>>());

        EmailMessage? capturedEmail = null;
        var resendMock = new Mock<IResend>();
        resendMock.Setup(r => r.EmailSendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((msg, _) => capturedEmail = msg)
            .ReturnsAsync(() =>
            {
                var response = (ResendResponse<Guid>)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ResendResponse<Guid>));
                foreach (var field in typeof(ResendResponse).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    if (field.FieldType == typeof(bool)) field.SetValue(response, true);
                    if (field.FieldType == typeof(System.Net.HttpStatusCode)) field.SetValue(response, System.Net.HttpStatusCode.OK);
                }
                return response;
            });

        var emailStrategy = new ResendEmailNotificationStrategy(
            resendMock.Object,
            Options.Create(new ResendOptions { ApiKey = "test-key", FromEmail = "Custodian <onboarding@resend.dev>" }),
            Mock.Of<IClientProfileRepository>(),
            Mock.Of<ILogger<ResendEmailNotificationStrategy>>());

        var dispatcher = new ClientNotificationDispatcher(
            new INotificationDeliveryStrategy[] { inAppStrategy, emailStrategy },
            new InMemoryEventDeduplicator(),
            Mock.Of<ILogger<ClientNotificationDispatcher>>());

        var envelope = new KafkaEnvelope(
            EventId: "evt-integration-999",
            EventType: "condition.attached",
            TenantId: tenantId.ToString(),
            OccurredAtUtc: DateTimeOffset.UtcNow,
            Payload: JsonSerializer.SerializeToElement(new
            {
                clientId,
                clientEmail = "client@acme.com",
                title = "Initial Scope Sign-off"
            })
        );

        // Act - Map and dispatch
        var mapped = mapper.MapToClientSafeMessage(envelope);
        var context = new NotificationContext
        {
            EventId = envelope.EventId,
            TenantId = tenantId,
            ClientId = mapped.ClientId,
            ClientEmail = mapped.ClientEmail,
            Subject = mapped.Subject,
            Message = mapped.Message,
            SourceEventType = envelope.EventType,
            Channels = new[] { NotificationChannel.InAppPortal, NotificationChannel.Email }
        };

        await dispatcher.DispatchAsync(context);

        // Assert
        // 1. Verify in DB
        var dbNotifications = (await repo.GetByClientAsync(clientId, tenantId)).ToList();
        Assert.Single(dbNotifications);
        Assert.Contains("Initial Scope Sign-off", dbNotifications[0].Message);
        Assert.False(dbNotifications[0].IsRead);

        // 2. Verify Email
        Assert.NotNull(capturedEmail);
        Assert.Contains("Approval Required: Initial Scope Sign-off", capturedEmail.Subject);
        Assert.Contains("awaiting your review on the portal", capturedEmail.HtmlBody);
    }
}
