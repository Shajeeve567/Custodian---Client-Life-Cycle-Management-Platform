using Custodian.Audit.Data;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.HashChain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// CSTD-42 (CSTD-241): Unit tests for audit event flag/archive metadata foundation.
/// Validates non-cryptographic metadata persistence, strict tenant isolation,
/// and proves that metadata mutations never alter original cryptographic fields or verification.
/// </summary>
public class AuditEventMetadataTests
{
    private readonly HashChainService _hashChainService = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _engagementId = Guid.NewGuid();

    private static AuditDbContext CreateDbContext(string name)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AuditDbContext(options);
    }

    private async Task<AuditEvent> CreateAndAppendEventAsync(
        AuditDbContext context,
        IAuditEventRepository repository,
        Guid tenantId,
        Guid engagementId,
        string step = "1")
    {
        var eventId = Guid.NewGuid();
        var result = await repository.AppendToChainAsync(tenantId, engagementId, eventId, previousHash =>
        {
            var utcNow = DateTime.UtcNow;
            utcNow = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
            var payload = $"{{\"step\":{step}}}";

            var hashInput = new EventHashInput
            {
                EventId = eventId,
                EngagementId = engagementId,
                TenantId = tenantId,
                Actor = "tester@custodian.com",
                Type = "Genesis",
                Timestamp = utcNow,
                Payload = payload,
                PreviousHash = previousHash
            };

            return new AuditEvent
            {
                EventId = eventId,
                EngagementId = engagementId,
                TenantId = tenantId,
                Actor = "tester@custodian.com",
                Type = "Genesis",
                Timestamp = utcNow,
                Payload = payload,
                Hash = _hashChainService.ComputeEventHash(hashInput),
                PreviousHash = previousHash
            };
        });

        return result.Event;
    }

    [Fact]
    public async Task Metadata_PersistsForExistingAuditEvent()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);

        var auditEvent = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId);
        var flaggedAt = DateTime.UtcNow;

        var metadata = new AuditEventMetadata
        {
            EventId = auditEvent.EventId,
            TenantId = _tenantId,
            IsFlagged = true,
            FlagReason = "Suspicious activity reported",
            FlaggedBy = "staff@custodian.com",
            FlaggedAt = flaggedAt,
            IsArchived = true,
            ArchiveReason = "Superseded by legal hold",
            ArchivedBy = "compliance@custodian.com",
            ArchivedAt = flaggedAt.AddMinutes(5),
            UpdatedAt = DateTime.UtcNow
        };

        var saved = await repository.SetMetadataAsync(metadata);
        Assert.NotNull(saved);

        var retrieved = await repository.GetMetadataAsync(auditEvent.EventId, _tenantId);
        Assert.NotNull(retrieved);
        Assert.True(retrieved!.IsFlagged);
        Assert.Equal("Suspicious activity reported", retrieved.FlagReason);
        Assert.Equal("staff@custodian.com", retrieved.FlaggedBy);
        Assert.Equal(flaggedAt, retrieved.FlaggedAt);
        Assert.True(retrieved.IsArchived);
        Assert.Equal("Superseded by legal hold", retrieved.ArchiveReason);
        Assert.Equal("compliance@custodian.com", retrieved.ArchivedBy);
    }

    [Fact]
    public async Task Metadata_Default_IsUnflaggedAndUnarchived()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var auditEvent = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId);

        var metadata = await repository.GetMetadataAsync(auditEvent.EventId, _tenantId);
        Assert.Null(metadata);

        var response = await service.GetEventByIdAsync(auditEvent.EventId, _tenantId);
        Assert.NotNull(response);
        Assert.False(response!.IsFlagged);
        Assert.Null(response.FlagReason);
        Assert.Null(response.FlaggedBy);
        Assert.Null(response.FlaggedAt);
        Assert.False(response.IsArchived);
        Assert.Null(response.ArchiveReason);
        Assert.Null(response.ArchivedBy);
        Assert.Null(response.ArchivedAt);
    }

    [Fact]
    public async Task Metadata_TenantIsolation_EnforcedOnLookupAndSave()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);

        var auditEvent = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId);

        // 1. Cross-tenant lookup returns null
        var crossTenantGet = await repository.GetMetadataAsync(auditEvent.EventId, _otherTenantId);
        Assert.Null(crossTenantGet);

        // 2. Cross-tenant save on existing event throws AuditChainConflictException
        var crossTenantMetadata = new AuditEventMetadata
        {
            EventId = auditEvent.EventId,
            TenantId = _otherTenantId,
            IsFlagged = true,
            FlagReason = "Malicious cross-tenant attempt"
        };

        await Assert.ThrowsAsync<AuditChainConflictException>(() => repository.SetMetadataAsync(crossTenantMetadata));

        // 3. Save on nonexistent event throws KeyNotFoundException
        var nonExistentMetadata = new AuditEventMetadata
        {
            EventId = Guid.NewGuid(),
            TenantId = _tenantId,
            IsFlagged = true
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.SetMetadataAsync(nonExistentMetadata));
    }

    [Fact]
    public async Task Metadata_Mutation_DoesNotChangeOriginalCryptographicFields()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);

        var original = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "42");

        // Record original cryptographic snapshot
        var originalPayload = original.Payload;
        var originalHash = original.Hash;
        var originalPreviousHash = original.PreviousHash;
        var originalSequence = original.SequenceNumber;
        var originalTimestamp = original.Timestamp;
        var originalActor = original.Actor;
        var originalType = original.Type;

        // Flag and archive via metadata
        var metadata = new AuditEventMetadata
        {
            EventId = original.EventId,
            TenantId = _tenantId,
            IsFlagged = true,
            FlagReason = "Audit review required",
            FlaggedBy = "reviewer@custodian.com",
            FlaggedAt = DateTime.UtcNow,
            IsArchived = true,
            ArchiveReason = "Archived per retention policy",
            ArchivedBy = "archiver@custodian.com",
            ArchivedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await repository.SetMetadataAsync(metadata);

        // Re-read event directly from DB
        var reloaded = await db.Events.AsNoTracking().FirstAsync(e => e.EventId == original.EventId);

        // Assert strictly untouched
        Assert.Equal(originalPayload, reloaded.Payload);
        Assert.Equal(originalHash, reloaded.Hash);
        Assert.Equal(originalPreviousHash, reloaded.PreviousHash);
        Assert.Equal(originalSequence, reloaded.SequenceNumber);
        Assert.Equal(originalTimestamp, reloaded.Timestamp);
        Assert.Equal(originalActor, reloaded.Actor);
        Assert.Equal(originalType, reloaded.Type);

        // Re-verify hash remains cryptographically valid
        var hashInput = new EventHashInput
        {
            EventId = reloaded.EventId,
            EngagementId = reloaded.EngagementId,
            TenantId = reloaded.TenantId,
            Actor = reloaded.Actor,
            Type = reloaded.Type,
            Timestamp = reloaded.Timestamp,
            Payload = reloaded.Payload,
            PreviousHash = reloaded.PreviousHash ?? string.Empty
        };
        Assert.True(_hashChainService.VerifyEventHash(hashInput, reloaded.Hash!));
    }

    [Fact]
    public async Task ArchivedEvent_RemainsInChainOrderRetrievalAndVerification()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        // Create a 3-event chain
        var e1 = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "1");
        var e2 = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "2");
        var e3 = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "3");

        // Archive the 2nd event
        await repository.SetMetadataAsync(new AuditEventMetadata
        {
            EventId = e2.EventId,
            TenantId = _tenantId,
            IsArchived = true,
            ArchivedBy = "compliance@custodian.com",
            ArchivedAt = DateTime.UtcNow
        });

        // 1. Chain-order retrieval still returns all 3 events
        var chainOrderEvents = await repository.GetByEngagementIdInChainOrderAsync(_tenantId, _engagementId);
        Assert.Equal(3, chainOrderEvents.Count);
        Assert.Equal(e1.EventId, chainOrderEvents[0].EventId);
        Assert.Equal(e2.EventId, chainOrderEvents[1].EventId);
        Assert.Equal(e3.EventId, chainOrderEvents[2].EventId);

        // 2. Chain verification succeeds and includes all 3 events
        var verificationResult = await service.VerifyChainAsync(_tenantId, _engagementId);
        Assert.True(verificationResult.IsVerified);
        Assert.Equal(3, verificationResult.Count);
        Assert.Null(verificationResult.BrokenAtEventId);
    }

    [Fact]
    public async Task Metadata_BatchLookup_ReturnsTenantScopedDictionary()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);

        var e1 = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "1");
        var e2 = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "2");
        var otherTenantEvent = await CreateAndAppendEventAsync(db, repository, _otherTenantId, Guid.NewGuid(), step: "1");

        await repository.SetMetadataAsync(new AuditEventMetadata
        {
            EventId = e1.EventId,
            TenantId = _tenantId,
            IsFlagged = true,
            FlagReason = "Flagged e1"
        });

        await repository.SetMetadataAsync(new AuditEventMetadata
        {
            EventId = otherTenantEvent.EventId,
            TenantId = _otherTenantId,
            IsFlagged = true,
            FlagReason = "Other tenant flag"
        });

        var dict = await repository.GetMetadataForEventsAsync(
            new[] { e1.EventId, e2.EventId, otherTenantEvent.EventId },
            _tenantId);

        Assert.Single(dict);
        Assert.True(dict.ContainsKey(e1.EventId));
        Assert.False(dict.ContainsKey(e2.EventId));
        Assert.False(dict.ContainsKey(otherTenantEvent.EventId));
    }

    [Fact]
    public async Task Metadata_UpdateExisting_UpdatesFieldsAndRefreshesTimestamp()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);

        var e1 = await CreateAndAppendEventAsync(db, repository, _tenantId, _engagementId, step: "1");

        var initial = await repository.SetMetadataAsync(new AuditEventMetadata
        {
            EventId = e1.EventId,
            TenantId = _tenantId,
            IsFlagged = true,
            FlagReason = "Initial flag",
            FlaggedBy = "staff1@custodian.com",
            FlaggedAt = DateTime.UtcNow.AddHours(-1)
        });

        var updated = await repository.SetMetadataAsync(new AuditEventMetadata
        {
            EventId = e1.EventId,
            TenantId = _tenantId,
            IsFlagged = true,
            FlagReason = "Updated reason",
            FlaggedBy = "staff1@custodian.com",
            FlaggedAt = initial.FlaggedAt,
            IsArchived = true,
            ArchivedBy = "archiver@custodian.com",
            ArchivedAt = DateTime.UtcNow
        });

        Assert.Equal("Updated reason", updated.FlagReason);
        Assert.True(updated.IsArchived);
        Assert.Equal("archiver@custodian.com", updated.ArchivedBy);
        Assert.True(updated.UpdatedAt >= initial.UpdatedAt);
    }
}
