using System.Text.Json;
using Confluent.Kafka;
using Custodian.Shared.Messaging;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Kafka;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for KafkaAuditPublisher. IProducer&lt;TKey,TValue&gt; is Confluent.Kafka's
/// own interface, so it's mocked directly with Moq — the same way IAuditPublisher itself
/// is mocked in EngagementsControllerUnitTests, no extra wrapper abstraction needed.
/// </summary>
public class KafkaAuditPublisherTests
{
    private readonly Mock<IProducer<string, string>> _mockProducer;
    private readonly KafkaAuditPublisher _publisher;
    private readonly KafkaProducerOptions _options = new() { Topic = "custodian.events", BootstrapServers = "localhost:9092", ClientId = "workflow-service" };

    public KafkaAuditPublisherTests()
    {
        _mockProducer = new Mock<IProducer<string, string>>();
        _publisher = new KafkaAuditPublisher(
            _mockProducer.Object,
            Options.Create(_options),
            NullLogger<KafkaAuditPublisher>.Instance);
    }

    private static DeliveryResult<string, string> FakeDeliveryResult(string topic) => new()
    {
        Topic = topic,
        Partition = new Partition(0),
        Offset = new Offset(1)
    };

    [Fact]
    public async Task PublishEventAsync_ValidCall_ProducesToConfiguredTopicWithEngagementIdAsKey()
    {
        // Arrange
        var engagementId = Guid.NewGuid();
        Message<string, string>? capturedMessage = null;
        string? capturedTopic = null;

        _mockProducer
            .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, string>, CancellationToken>((topic, message, _) =>
            {
                capturedTopic = topic;
                capturedMessage = message;
            })
            .ReturnsAsync((string topic, Message<string, string> _, CancellationToken _) => FakeDeliveryResult(topic));

        // Act
        await _publisher.PublishEventAsync(engagementId, "tenant-001", "System", "StageChange", new { fromStage = "Onboarding", toStage = "DocumentCollection" });

        // Assert: correct topic, key = engagementId (partitioning/ordering guarantee)
        Assert.Equal("custodian.events", capturedTopic);
        Assert.NotNull(capturedMessage);
        Assert.Equal(engagementId.ToString(), capturedMessage!.Key);

        // Assert: the value deserializes back into a KafkaEnvelope with the right EventType/TenantId
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(capturedMessage.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(envelope);
        Assert.Equal("StageChange", envelope!.EventType);
        Assert.Equal("tenant-001", envelope.TenantId);

        var enrichedPayload = envelope.ReadPayload<EngagementEventPayload>();
        Assert.Equal(engagementId, enrichedPayload.EngagementId);
        Assert.Equal("System", enrichedPayload.Actor);
    }

    [Fact]
    public async Task PublishGenesisEventAsync_ValidEngagement_ProducesGenesisEventWithStatusAndStage()
    {
        // Arrange
        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Draft,
            Stage = EngagementStage.Onboarding
        };

        Message<string, string>? capturedMessage = null;

        _mockProducer
            .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, string>, CancellationToken>((_, message, _) => capturedMessage = message)
            .ReturnsAsync((string topic, Message<string, string> _, CancellationToken _) => FakeDeliveryResult(topic));

        // Act
        await _publisher.PublishGenesisEventAsync(engagement, "tenant-001");

        // Assert
        var envelope = JsonSerializer.Deserialize<KafkaEnvelope>(capturedMessage!.Value, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal("Genesis", envelope!.EventType);

        var enrichedPayload = envelope.ReadPayload<EngagementEventPayload>();
        var data = enrichedPayload.Data;
        Assert.Equal("Draft", data.GetProperty("status").GetString());
        Assert.Equal("Onboarding", data.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task PublishEventAsync_ProducerThrows_DoesNotPropagateException()
    {
        // Arrange: a Kafka/broker failure must not fail the primary business transaction
        _mockProducer
            .Setup(p => p.ProduceAsync(It.IsAny<string>(), It.IsAny<Message<string, string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProduceException<string, string>(new Error(ErrorCode.Local_Transport, "broker unreachable"), null!));

        // Act & Assert: no exception should propagate out of PublishEventAsync
        var exception = await Record.ExceptionAsync(() =>
            _publisher.PublishEventAsync(Guid.NewGuid(), "tenant-001", "System", "StageChange", new { }));

        Assert.Null(exception);
    }
}
