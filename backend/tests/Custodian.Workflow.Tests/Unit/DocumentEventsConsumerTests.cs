using System.Text.Json;
using Custodian.Shared.Messaging;
using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// CSTD-19 (19-N5): service-side document sync. Drives DocumentEventsConsumer.ProcessMessageAsync
/// directly (no broker) against the real ClientActionService on an in-memory database.
/// </summary>
public class DocumentEventsConsumerTests
{
    private const string TenantId = "tenant-001";
    private readonly Guid _engagementId = Guid.NewGuid();
    private readonly Mock<IAuditPublisher> _auditPublisher = new();
    private readonly ServiceProvider _provider;
    private readonly DocumentEventsConsumer _consumer;

    public DocumentEventsConsumerTests()
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IClientActionService>(sp =>
            new ClientActionService(sp.GetRequiredService<WorkflowDbContext>(), _auditPublisher.Object));
        _provider = services.BuildServiceProvider();

        _consumer = new DocumentEventsConsumer(
            Options.Create(new KafkaConsumerOptions()),
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DocumentEventsConsumer>.Instance);
    }

    private async Task<ClientAction> SeedActionAsync(Guid documentId, string status, string tenantId = TenantId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        if (!await db.Engagements.AnyAsync(e => e.EngagementId == _engagementId))
        {
            db.Engagements.Add(new Engagement
            {
                EngagementId = _engagementId,
                TenantId = TenantId,
                ClientId = "client-001",
                StaffId = "staff-001",
                Status = EngagementStatus.Started,
                Stage = EngagementStage.DocumentCollection
            });
        }

        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = tenantId,
            Title = "Passport",
            Type = ClientActionType.KycDocument,
            Status = status,
            AssignedToRole = "Client",
            StageNumber = 2,
            LinkedDocumentId = documentId,
            SourceType = ClientActionSourceType.Lifecycle
        };
        db.ClientActions.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private async Task<ClientAction> ReloadAsync(Guid actionId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        return await db.ClientActions.AsNoTracking().SingleAsync(a => a.ActionId == actionId);
    }

    private string Message(string eventType, Guid documentId, string? rejectionReason = null) =>
        JsonSerializer.Serialize(KafkaEnvelope.Create(eventType, TenantId, new
        {
            documentId,
            engagementId = _engagementId,
            tenantId = TenantId,
            verificationStatus = eventType == EventTypes.DocumentVerified ? "Verified" : "Rejected",
            verifiedBy = "staff-042",
            notes = "Looks good",
            rejectionReason
        }));

    private void VerifyStatusChangedEvents(Times times) =>
        _auditPublisher.Verify(p => p.PublishEventAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "ClientActionStatusChanged", It.IsAny<object>()), times);

    [Fact]
    public async Task DocumentVerified_CompletesLinkedUploadedAction()
    {
        var documentId = Guid.NewGuid();
        var action = await SeedActionAsync(documentId, ClientActionStatus.Uploaded);

        await _consumer.ProcessMessageAsync(Message(EventTypes.DocumentVerified, documentId));

        var reloaded = await ReloadAsync(action.ActionId);
        Assert.Equal(ClientActionStatus.Completed, reloaded.Status);
        Assert.Equal("staff-042", reloaded.CompletedByActor);
        VerifyStatusChangedEvents(Times.Once());
    }

    [Fact]
    public async Task DocumentVerificationRejected_RejectsLinkedUploadedAction()
    {
        var documentId = Guid.NewGuid();
        var action = await SeedActionAsync(documentId, ClientActionStatus.Uploaded);

        await _consumer.ProcessMessageAsync(Message(EventTypes.DocumentVerificationRejected, documentId, "Blurry scan"));

        var reloaded = await ReloadAsync(action.ActionId);
        Assert.Equal(ClientActionStatus.Rejected, reloaded.Status);
        Assert.Contains("Blurry scan", reloaded.SourceMetadata);
    }

    [Theory]
    [InlineData(EventTypes.DocumentVerified)]
    [InlineData(EventTypes.DocumentVerificationRejected)]
    public async Task RedeliveredMessage_IsAppliedOnlyOnce(string eventType)
    {
        var documentId = Guid.NewGuid();
        await SeedActionAsync(documentId, ClientActionStatus.Uploaded);
        var message = Message(eventType, documentId, "Blurry scan");

        await _consumer.ProcessMessageAsync(message);
        await _consumer.ProcessMessageAsync(message);

        VerifyStatusChangedEvents(Times.Once());
    }

    [Fact]
    public async Task OtherTenantsAction_IsNotTouched()
    {
        var documentId = Guid.NewGuid();
        var foreign = await SeedActionAsync(documentId, ClientActionStatus.Uploaded, tenantId: "tenant-other");

        await _consumer.ProcessMessageAsync(Message(EventTypes.DocumentVerified, documentId));

        Assert.Equal(ClientActionStatus.Uploaded, (await ReloadAsync(foreign.ActionId)).Status);
        VerifyStatusChangedEvents(Times.Never());
    }

    [Fact]
    public async Task UnrelatedEventType_IsIgnored()
    {
        var documentId = Guid.NewGuid();
        var action = await SeedActionAsync(documentId, ClientActionStatus.Uploaded);

        await _consumer.ProcessMessageAsync(Message("ClientActionStatusChanged", documentId));

        Assert.Equal(ClientActionStatus.Uploaded, (await ReloadAsync(action.ActionId)).Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"EventType\":\"document.verified\",\"TenantId\":\"tenant-001\",\"Payload\":{}}")]
    [InlineData("")]
    public async Task MalformedOrIncompleteMessage_DoesNotThrow(string raw)
    {
        var exception = await Record.ExceptionAsync(() => _consumer.ProcessMessageAsync(raw));

        Assert.Null(exception);
        VerifyStatusChangedEvents(Times.Never());
    }
}
