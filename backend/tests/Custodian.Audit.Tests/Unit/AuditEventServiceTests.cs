using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

public class AuditEventServiceTests
{
    private readonly Mock<IAuditEventRepository> _mockRepo;
    private readonly AuditEventService _service;
    private readonly Guid _testTenantId = Guid.NewGuid();
    private readonly Guid _testEngagementId = Guid.NewGuid();

    public AuditEventServiceTests()
    {
        _mockRepo = new Mock<IAuditEventRepository>();
        _service = new AuditEventService(_mockRepo.Object);
    }

    [Fact]
    public async Task RecordEventAsync_ValidPayload_CreatesAndReturnsEventResponse()
    {
        // Arrange
        var request = new CreateAuditEventRequest
        {
            EngagementId = _testEngagementId,
            TenantId = _testTenantId,
            Actor = "user@custodian.com",
            Type = "EngagementCreated",
            Payload = "{\"status\":\"Draft\",\"client\":\"Acme Corp\"}"
        };

        _mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>()))
            .ReturnsAsync((AuditEvent e) => e);

        // Act
        var result = await _service.RecordEventAsync(request, _testTenantId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(_testEngagementId, result.EngagementId);
        Assert.Equal(_testTenantId, result.TenantId);
        Assert.Equal("user@custodian.com", result.Actor);
        Assert.Equal("EngagementCreated", result.Type);
        Assert.False(string.IsNullOrWhiteSpace(result.Hash));
        _mockRepo.Verify(r => r.AddAsync(It.IsAny<AuditEvent>()), Times.Once);
    }

    [Fact]
    public async Task RecordEventAsync_InvalidJsonPayload_ThrowsArgumentException()
    {
        // Arrange
        var request = new CreateAuditEventRequest
        {
            EngagementId = _testEngagementId,
            Actor = "user@custodian.com",
            Type = "EngagementCreated",
            Payload = "NOT_A_VALID_JSON"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RecordEventAsync(request, _testTenantId));

        Assert.Contains("valid JSON", ex.Message);
        _mockRepo.Verify(r => r.AddAsync(It.IsAny<AuditEvent>()), Times.Never);
    }

    [Fact]
    public async Task RecordEventAsync_MissingEngagementId_ThrowsArgumentException()
    {
        // Arrange
        var request = new CreateAuditEventRequest
        {
            EngagementId = Guid.Empty,
            Actor = "user@custodian.com",
            Type = "EngagementCreated",
            Payload = "{}"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.RecordEventAsync(request, _testTenantId));
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

        _mockRepo.Setup(r => r.GetByIdAsync(suppliedEventId, _testTenantId))
            .ReturnsAsync((AuditEvent?)null);
        _mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEvent>()))
            .ReturnsAsync((AuditEvent e) => e);

        // Act
        var result = await _service.RecordEventAsync(request, _testTenantId);

        // Assert: the row is recorded under the supplied EventId, not a freshly generated one
        Assert.Equal(suppliedEventId, result.EventId);
        _mockRepo.Verify(r => r.AddAsync(It.Is<AuditEvent>(e => e.EventId == suppliedEventId)), Times.Once);
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

        _mockRepo.Setup(r => r.GetByIdAsync(existingEventId, _testTenantId))
            .ReturnsAsync(existing);

        // Act
        var result = await _service.RecordEventAsync(request, _testTenantId);

        // Assert: idempotent no-op — the existing row is returned, nothing new is inserted
        Assert.Equal(existingEventId, result.EventId);
        Assert.Equal(5, result.SequenceNumber);
        _mockRepo.Verify(r => r.AddAsync(It.IsAny<AuditEvent>()), Times.Never);
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
