using Custodian.Workflow.Configuration;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Stall;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// CSTD-33 (33-N1, 33-4, AC5/AC6): stall episodes are persisted, so action.overdue fires exactly once
/// per episode (even across restarts / instances), and ending the stall publishes StallResolved once.
/// </summary>
public class StallRecorderTests
{
    private const string Tenant = "tenant-001";
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly IStallDetectionService _detection = new StallDetectionService(Options.Create(new SlaOptions { DefaultOverdueHours = 72 }));
    private readonly Guid _engagementId = Guid.NewGuid();

    private WorkflowDbContext NewDb() => new(new DbContextOptionsBuilder<WorkflowDbContext>().UseInMemoryDatabase(_dbName).Options);

    // A new recorder + DbContext per call, like separate requests, restarts or service instances.
    private async Task<StallSnapshot> SyncAsync(string tenant = Tenant)
    {
        using var db = NewDb();
        var actions = await db.ClientActions.AsNoTracking().Where(a => a.EngagementId == _engagementId && a.TenantId == tenant).ToListAsync();
        var recorder = new StallRecorder(db, _detection, _audit.Object, _clock, NullLogger<StallRecorder>.Instance);
        var result = await recorder.SyncAsync(tenant, new[] { new StallSyncInput(_engagementId, "client-1", actions) });
        return result[_engagementId];
    }

    private async Task<ClientAction> SeedOverdueActionAsync(TimeSpan overdueBy, string tenant = Tenant)
    {
        using var db = NewDb();
        if (!await db.Engagements.AnyAsync(e => e.EngagementId == _engagementId))
        {
            db.Engagements.Add(new Engagement
            {
                EngagementId = _engagementId,
                TenantId = tenant,
                ClientId = "client-1",
                StaffId = "staff-1",
                Status = EngagementStatus.Started,
                Stage = EngagementStage.DocumentCollection
            });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = tenant,
            Title = "Sign engagement letter",
            Type = "CustomTask",
            Source = "Test",
            Status = ClientActionStatus.Pending,
            StageNumber = 2,
            AssignedToRole = "Client",
            CreatedAt = now.AddDays(-10),
            ActivatedAt = now.AddDays(-10),
            DeadlineUtc = now - overdueBy
        };
        db.ClientActions.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private void VerifyEvents(string type, Times times) =>
        _audit.Verify(a => a.PublishEventAsync(_engagementId, It.IsAny<string>(), It.IsAny<string>(), type, It.IsAny<object>()), times);

    [Fact]
    public async Task RepeatedReads_ByDifferentInstances_RecordOneStall_AndPublishOverdueOnce()
    {
        await SeedOverdueActionAsync(TimeSpan.FromHours(5));

        for (var i = 0; i < 5; i++)
        {
            var snapshot = await SyncAsync();
            Assert.True(snapshot.Status.IsStalled);
            Assert.Equal(1, snapshot.OpenStallCount);
        }

        using var db = NewDb();
        var record = Assert.Single(db.StallRecords);
        Assert.NotNull(record.OverdueEventPublishedAt);
        VerifyEvents(StallRecorder.OverdueEventType, Times.Once());
    }

    [Fact]
    public async Task StalledSince_IsWhenTheStallWasFirstDetected()
    {
        await SeedOverdueActionAsync(TimeSpan.FromHours(5));
        var detectedAt = _clock.GetUtcNow().UtcDateTime;

        await SyncAsync();
        _clock.Advance(TimeSpan.FromHours(3));
        var later = await SyncAsync();

        Assert.Equal(detectedAt, later.StalledSinceUtc);
    }

    [Theory]
    [InlineData(ClientActionStatus.Completed, StallResolution.ActionCompleted)]
    [InlineData(ClientActionStatus.Cancelled, StallResolution.ActionCancelled)]
    [InlineData(ClientActionStatus.Uploaded, StallResolution.ActionSubmitted)]
    public async Task StatusChange_ResolvesTheStallImmediately_AndPublishesStallResolvedOnce(string newStatus, string expectedResolution)
    {
        var action = await SeedOverdueActionAsync(TimeSpan.FromHours(5));
        await SyncAsync();

        using (var db = NewDb())
        {
            var service = new ClientActionService(db, _audit.Object);
            if (newStatus == ClientActionStatus.Completed)
            {
                await service.CompleteActionAsync(_engagementId, action.ActionId, Tenant, new CompleteClientActionDto { CompletedByActor = "client-1" });
            }
            else if (newStatus == ClientActionStatus.Cancelled)
            {
                await service.CancelActionAsync(_engagementId, action.ActionId, Tenant, "No longer needed", "staff-1");
            }
            else
            {
                await service.UploadEvidenceAsync(_engagementId, action.ActionId, Tenant, new UploadActionEvidenceDto { DocumentId = Guid.NewGuid(), UploaderActor = "client-1" });
            }
        }

        using (var db = NewDb())
        {
            var record = Assert.Single(db.StallRecords);
            Assert.NotNull(record.ResolvedAtUtc);
            Assert.Null(record.OpenActionId);
            Assert.Equal(expectedResolution, record.Resolution);
        }

        await SyncAsync(); // a later read must not resolve or publish again
        VerifyEvents(StallRecorder.ResolvedEventType, Times.Once());
    }

    [Fact]
    public async Task DeadlineExtended_ResolvesTheStall_AndMissingItAgainIsANewEpisodeWithANewEvent()
    {
        var action = await SeedOverdueActionAsync(TimeSpan.FromHours(5));
        await SyncAsync();

        using (var db = NewDb())
        {
            var tracked = await db.ClientActions.SingleAsync(a => a.ActionId == action.ActionId);
            tracked.DeadlineUtc = _clock.GetUtcNow().UtcDateTime.AddHours(24);
            await db.SaveChangesAsync();
        }

        var afterExtension = await SyncAsync();
        Assert.False(afterExtension.Status.IsStalled);
        using (var db = NewDb())
        {
            Assert.Equal(StallResolution.DeadlineExtended, (await db.StallRecords.SingleAsync()).Resolution);
        }

        _clock.Advance(TimeSpan.FromHours(25));
        var missedAgain = await SyncAsync();

        Assert.True(missedAgain.Status.IsStalled);
        using (var db = NewDb())
        {
            Assert.Equal(2, await db.StallRecords.CountAsync());
            Assert.Equal(2, (await db.StallRecords.ToListAsync()).Select(r => r.StallId).Distinct().Count());
        }
        VerifyEvents(StallRecorder.OverdueEventType, Times.Exactly(2));
        VerifyEvents(StallRecorder.ResolvedEventType, Times.Once());
    }

    [Fact]
    public async Task ClosingTheEngagement_ResolvesItsStalls()
    {
        await SeedOverdueActionAsync(TimeSpan.FromHours(5));
        await SyncAsync();

        using (var db = NewDb())
        {
            await new StallRecorder(db, _detection, _audit.Object, _clock, NullLogger<StallRecorder>.Instance)
                .ResolveForEngagementAsync(_engagementId, Tenant, StallResolution.EngagementClosed);
        }

        using var check = NewDb();
        Assert.Equal(StallResolution.EngagementClosed, (await check.StallRecords.SingleAsync()).Resolution);
        VerifyEvents(StallRecorder.ResolvedEventType, Times.Once());
    }

    [Fact]
    public async Task AnotherTenantsSync_NeverTouchesThisTenantsStall()
    {
        await SeedOverdueActionAsync(TimeSpan.FromHours(5));
        await SyncAsync();

        // Tenant B reads the same engagement id: it sees no actions and must not resolve tenant A's stall.
        var other = await SyncAsync("tenant-other");

        Assert.False(other.Status.IsStalled);
        using var db = NewDb();
        Assert.Null((await db.StallRecords.SingleAsync()).ResolvedAtUtc);
    }

    [Fact]
    public async Task ExactlyAtTheDeadline_IsNotAStall()
    {
        await SeedOverdueActionAsync(TimeSpan.Zero);

        var snapshot = await SyncAsync();

        Assert.False(snapshot.Status.IsStalled);
        using var db = NewDb();
        Assert.Empty(db.StallRecords);
    }
}
