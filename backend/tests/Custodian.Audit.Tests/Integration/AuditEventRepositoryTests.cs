using Custodian.Audit.Data;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Audit.Tests.Integration;

public class AuditEventRepositoryTests
{
    private AuditDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new AuditDbContext(options);
    }

    [Fact]
    public async Task AddAsync_SavesAuditEventToDatabase()
    {
        // Arrange
        using var context = CreateInMemoryDbContext();
        var repository = new AuditEventRepository(context);

        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            Actor = "user@custodian.com",
            Type = "Genesis",
            Timestamp = DateTime.UtcNow,
            Payload = "{\"test\":true}"
        };

        // Act
        var result = await repository.AddAsync(auditEvent);

        // Assert
        Assert.NotNull(result);
        var dbEvent = await context.Events.FirstOrDefaultAsync(e => e.EventId == auditEvent.EventId);
        Assert.NotNull(dbEvent);
        Assert.Equal("Genesis", dbEvent.Type);
    }

    [Fact]
    public async Task GetByEngagementIdAsync_ReturnsEventsOrderedBySequenceNumber()
    {
        // Arrange
        using var context = CreateInMemoryDbContext();
        var repository = new AuditEventRepository(context);

        var engagementId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var event1 = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "system",
            Type = "Genesis",
            Payload = "{}",
            SequenceNumber = 1
        };

        var event2 = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "staff",
            Type = "StatusUpdated",
            Payload = "{}",
            SequenceNumber = 2
        };

        await repository.AddAsync(event1);
        await repository.AddAsync(event2);

        // Act
        var results = (await repository.GetByEngagementIdAsync(engagementId, tenantId)).ToList();

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal("Genesis", results[0].Type);
        Assert.Equal("StatusUpdated", results[1].Type);
    }

    [Fact]
    public async Task GetByTenantIdAsync_FiltersOutOtherTenantEvents()
    {
        // Arrange
        using var context = CreateInMemoryDbContext();
        var repository = new AuditEventRepository(context);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var eventA = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = Guid.NewGuid(),
            TenantId = tenantA,
            Actor = "userA",
            Type = "TypeA",
            Payload = "{}"
        };

        var eventB = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = Guid.NewGuid(),
            TenantId = tenantB,
            Actor = "userB",
            Type = "TypeB",
            Payload = "{}"
        };

        await repository.AddAsync(eventA);
        await repository.AddAsync(eventB);

        // Act
        var resultsA = (await repository.GetByTenantIdAsync(tenantA)).ToList();

        // Assert
        Assert.Single(resultsA);
        Assert.Equal("TypeA", resultsA[0].Type);
    }
}
