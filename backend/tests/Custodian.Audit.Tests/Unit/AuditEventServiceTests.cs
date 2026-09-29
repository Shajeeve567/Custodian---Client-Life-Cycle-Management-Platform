using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.HashChain;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

public class AuditEventServiceTests
{
    private readonly Mock<IAuditEventRepository> _mockRepo;
    private readonly Mock<IHashChainService> _mockHashChain;
    private readonly AuditEventService _service;
    private readonly Guid _testTenantId = Guid.NewGuid();
    private readonly Guid _testEngagementId = Guid.NewGuid();

    public AuditEventServiceTests()
    {
        _mockRepo = new Mock<IAuditEventRepository>();
        _mockHashChain = new Mock<IHashChainService>();
        _mockHashChain.Setup(h => h.GenesisHash).Returns(new string('0', 64));
        _mockHashChain.Setup(h => h.ComputeEventHash(It.IsAny<EventHashInput>())).Returns(new string('a', 64));
        // Default: a new engagement chain; the repository hands the builder the genesis hash.
        _mockRepo
            .Setup(r => r.AppendToChainAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<string, AuditEvent>>()))
            .ReturnsAsync((Guid _, Guid _, Guid _, Func<string, AuditEvent> build) => new ChainAppendResult(build(new string('0', 64)), true));

        _service = new AuditEventService(_mockRepo.Object, _mockHashChain.Object);
    }

    [Fact]
    public async Task RecordEventAsync_ValidPayload_CreatesAndReturnsEventResponse()
    {
        var request = new CreateAuditEventRequest
        {
            EngagementId = _testEngagementId,
            TenantId = _testTenantId,
            Actor = "user@custodian.com",
            Type = "EngagementCreated",
            Payload = "{\"status\":\"Draft\",\"client\":\"Acme Corp\"}"
        };

        var result = await _service.RecordEventAsync(request, _testTenantId);

        Assert.NotNull(result);
        Assert.Equal(_testEngagementId, result.EngagementId);
        Assert.Equal(_testTenantId, result.TenantId);
        Assert.Equal("user@custodian.com", result.Actor);
        Assert.Equal("EngagementCreated", result.Type);
        Assert.False(string.IsNullOrWhiteSpace(result.Hash));
        Assert.Equal(new string('0', 64), result.PreviousHash);  // genesis for a new engagement
        _mockRepo.Verify(r => r.AppendToChainAsync(_testTenantId, _testEngagementId, result.EventId, It.IsAny<Func<string, AuditEvent>>()), Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_LinksToThePreviousHashTheRepositoryLocked()
    {
        var previous = new string('b', 64);
        _mockRepo
            .Setup(r => r.AppendToChainAsync(_testTenantId, _testEngagementId, It.IsAny<Guid>(), It.IsAny<Func<string, AuditEvent>>()))
            .ReturnsAsync((Guid _, Guid _, Guid _, Func<string, AuditEvent> build) => new ChainAppendResult(build(previous), true));

        var result = await _service.RecordEventAsync(new CreateAuditEventRequest
        {
            EngagementId = _testEngagementId,
            Actor = "System",
            Type = "StageChange",
            Payload = "{}"
        }, _testTenantId);

        Assert.Equal(previous, result.PreviousHash);
        _mockHashChain.Verify(h => h.ComputeEventHash(It.Is<EventHashInput>(i => i.PreviousHash == previous)), Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_MissingActor_ThrowsArgumentException()
    {
        // Arrange
        var request = new CreateAuditEventRequest
        {
            EngagementId = _testEngagementId,
            Actor = "",
            Type = "EngagementCreated",
            Payload = "{}"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RecordEventAsync(request, _testTenantId));
    }

    [Fact]
    public async Task RecordEventAsync_SuppliedEventIdNotYetRecorded_UsesSuppliedEventId()
    {
        // Arrange: caller (e.g. the Kafka consumer) supplies an EventId that hasn't been seen before
        var suppliedEventId = Guid.NewGuid();
        var request = new CreateAuditEventRequest
        {
            EventId = suppliedEventId,
            EngagementId = _testEngagementId,
            TenantId = _testTenantId,
            Actor = "System",
            Type = "StageChange",
            Payload = "{}"
        };

        // Act
        var result = await _service.RecordEventAsync(request, _testTenantId);

        // Assert: the row is recorded under the supplied EventId, not a freshly generated one
        Assert.Equal(suppliedEventId, result.EventId);
        _mockRepo.Verify(r => r.AppendToChainAsync(_testTenantId, _testEngagementId, suppliedEventId, It.IsAny<Func<string, AuditEvent>>()), Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_SuppliedEventIdAlreadyRecorded_ReturnsExistingWithoutInsertingDuplicate()
    {
        // Arrange: a redelivered Kafka message carrying an EventId already recorded
        var existingEventId = Guid.NewGuid();
        var existing = new AuditEvent
        {
            EventId = existingEventId,
            EngagementId = _testEngagementId,
            TenantId = _testTenantId,
            Actor = "System",
            Type = "StageChange",
            Payload = "{}",
            SequenceNumber = 5
        };

        var request = new CreateAuditEventRequest
        {
            EventId = existingEventId,
            EngagementId = _testEngagementId,
            TenantId = _testTenantId,
            Actor = "System",
            Type = "StageChange",
            Payload = "{}"
        };

        // The repository finds the id inside its transaction and returns the stored row unchanged.
        _mockRepo
            .Setup(r => r.AppendToChainAsync(_testTenantId, _testEngagementId, existingEventId, It.IsAny<Func<string, AuditEvent>>()))
            .ReturnsAsync(new ChainAppendResult(existing, false));

        // Act
        var result = await _service.RecordEventAsync(request, _testTenantId);

        // Assert: idempotent no-op — the existing row is returned, nothing new is inserted
        Assert.Equal(existingEventId, result.EventId);
        Assert.Equal(5, result.SequenceNumber);
        _mockHashChain.Verify(h => h.ComputeEventHash(It.IsAny<EventHashInput>()), Times.Never);
    }

    [Fact]
    public async Task GetEventsByEngagementAsync_ReturnsTenantScopedEvents()
    {
        // Arrange
        var events = new List<AuditEvent>
        {
            new AuditEvent
            {
                EventId = Guid.NewGuid(),
                EngagementId = _testEngagementId,
                TenantId = _testTenantId,
                Actor = "admin",
                Type = "Genesis",
                Payload = "{}",
                SequenceNumber = 1
            }
        };

        _mockRepo.Setup(r => r.GetByEngagementIdAsync(_testEngagementId, _testTenantId))
            .ReturnsAsync(events);

        // Act
        var results = await _service.GetEventsByEngagementAsync(_testEngagementId, _testTenantId);

        // Assert
        Assert.Single(results);
        Assert.Equal("Genesis", results.First().Type);
    }
}
