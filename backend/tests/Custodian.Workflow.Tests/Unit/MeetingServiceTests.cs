using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Meetings;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class MeetingServiceTests : IDisposable
{
    private readonly WorkflowDbContext _db;
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly MeetingService _svc;
    private readonly Guid _engagementId = Guid.NewGuid();
    private const string Tenant = "tenant-test";
    private const string Actor = "staff@test";

    public MeetingServiceTests()
    {
        var opts = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase("meetings-" + Guid.NewGuid().ToString("N"))
            .Options;
        _db = new WorkflowDbContext(opts);

        _db.Engagements.Add(new Engagement
        {
            EngagementId = _engagementId,
            TenantId = Tenant,
            ClientId = "client-1",
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _svc = new MeetingService(_db, _audit.Object);
    }

    public void Dispose() => _db.Dispose();

    private static CreateMeetingRequest ValidRequest() => new()
    {
        Type = MeetingType.Normal,
        Purpose = "Kickoff call",
        ScheduledAtUtc = DateTime.UtcNow.AddDays(2),
        DurationMinutes = 30,
        Participants = new List<string> { "a@x.test", "b@x.test" },
        Importance = MeetingImportance.Important,
    };

    [Fact]
    public async Task CreateAsync_ValidRequest_PersistsAndPublishes()
    {
        var result = await _svc.CreateAsync(_engagementId, Tenant, Actor, ValidRequest());

        Assert.NotEqual(Guid.Empty, result.MeetingId);
        Assert.Equal(MeetingStatus.Scheduled, result.Status);
        Assert.Equal(MeetingImportance.Important, result.Importance);
        Assert.Equal(2, result.Participants.Count);
        Assert.Single(_db.Meetings);

        _audit.Verify(a => a.PublishEventAsync(
            _engagementId, Tenant, Actor, "meeting.scheduled", It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateAsync_PastDate_Throws()
    {
        var req = ValidRequest();
        req.ScheduledAtUtc = DateTime.UtcNow.AddHours(-1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.CreateAsync(_engagementId, Tenant, Actor, req));

        Assert.Contains("future", ex.Message);
        Assert.Empty(_db.Meetings);
    }

    [Fact]
    public async Task CreateAsync_InvalidType_Throws()
    {
        var req = ValidRequest();
        req.Type = "Bogus";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.CreateAsync(_engagementId, Tenant, Actor, req));

        Assert.Contains("Invalid meeting type", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_InvalidImportance_Throws()
    {
        var req = ValidRequest();
        req.Importance = "Bogus";

        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.CreateAsync(_engagementId, Tenant, Actor, req));
    }

    [Fact]
    public async Task CreateAsync_UnknownEngagement_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.CreateAsync(Guid.NewGuid(), Tenant, Actor, ValidRequest()));
    }

    [Fact]
    public async Task CreateAsync_CrossTenant_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.CreateAsync(_engagementId, "other-tenant", Actor, ValidRequest()));
    }

    [Fact]
    public async Task UpdateStatusAsync_RejectsScheduled()
    {
        var created = await _svc.CreateAsync(_engagementId, Tenant, Actor, ValidRequest());

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.UpdateStatusAsync(_engagementId, created.MeetingId, Tenant, Actor, "Scheduled"));

        Assert.Contains("Invalid status", ex.Message);
    }

    [Fact]
    public async Task UpdateStatusAsync_RejectsInvalidValue()
    {
        var created = await _svc.CreateAsync(_engagementId, Tenant, Actor, ValidRequest());

        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.UpdateStatusAsync(_engagementId, created.MeetingId, Tenant, Actor, "Bogus"));
    }

    [Fact]
    public async Task UpdateStatusAsync_MarksCompleted_PublishesEvent()
    {
        var created = await _svc.CreateAsync(_engagementId, Tenant, Actor, ValidRequest());

        var result = await _svc.UpdateStatusAsync(_engagementId, created.MeetingId, Tenant, Actor, MeetingStatus.Completed);

        Assert.NotNull(result);
        Assert.Equal(MeetingStatus.Completed, result!.Status);
        _audit.Verify(a => a.PublishEventAsync(
            _engagementId, Tenant, Actor, "meeting.completed", It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task RescheduleAsync_CreatesLinkedNewRecord_MarksOriginalRescheduled()
    {
        var original = await _svc.CreateAsync(_engagementId, Tenant, Actor, ValidRequest());
        var newTime = DateTime.UtcNow.AddDays(5);

        var replacement = await _svc.RescheduleAsync(
            _engagementId, original.MeetingId, Tenant, Actor,
            new RescheduleMeetingRequest { NewScheduledAtUtc = newTime, Reason = "Client pushed" });

        Assert.NotNull(replacement);
        Assert.NotEqual(original.MeetingId, replacement!.MeetingId);
        Assert.Equal(original.MeetingId, replacement.RescheduledFromMeetingId);
        Assert.Equal(newTime, replacement.ScheduledAtUtc);
        Assert.Equal(MeetingStatus.Scheduled, replacement.Status);
        Assert.Equal("Client pushed", replacement.RescheduleReason);

        var originalFromDb = await _db.Meetings.AsNoTracking()
            .FirstAsync(m => m.MeetingId == original.MeetingId);
        Assert.Equal(MeetingStatus.Rescheduled, originalFromDb.Status);
    }

    [Fact]
    public async Task RescheduleAsync_RejectsPastDate()
    {
        var original = await _svc.CreateAsync(_engagementId, Tenant, Actor, ValidRequest());

        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.RescheduleAsync(_engagementId, original.MeetingId, Tenant, Actor,
                new RescheduleMeetingRequest { NewScheduledAtUtc = DateTime.UtcNow.AddHours(-1) }));
    }

    [Fact]
    public async Task RescheduleAsync_UnknownMeeting_ReturnsNull()
    {
        var result = await _svc.RescheduleAsync(_engagementId, Guid.NewGuid(), Tenant, Actor,
            new RescheduleMeetingRequest { NewScheduledAtUtc = DateTime.UtcNow.AddDays(1) });

        Assert.Null(result);
    }

    [Fact]
    public async Task ListMissedForTenantAsync_ReturnsOnlyImportantMissedOrPastDue()
    {
        var pastDueImportant = ValidRequest();
        pastDueImportant.Importance = MeetingImportance.Important;
        pastDueImportant.ScheduledAtUtc = DateTime.UtcNow.AddHours(1);
        var pm = await _svc.CreateAsync(_engagementId, Tenant, Actor, pastDueImportant);

        // Force it past-due
        var row = await _db.Meetings.FirstAsync(m => m.MeetingId == pm.MeetingId);
        row.ScheduledAtUtc = DateTime.UtcNow.AddHours(-1);
        await _db.SaveChangesAsync();

        // Non-important past-due — should not appear
        var pastDueNormal = ValidRequest();
        pastDueNormal.Importance = MeetingImportance.Normal;
        pastDueNormal.ScheduledAtUtc = DateTime.UtcNow.AddHours(1);
        var pn = await _svc.CreateAsync(_engagementId, Tenant, Actor, pastDueNormal);
        var row2 = await _db.Meetings.FirstAsync(m => m.MeetingId == pn.MeetingId);
        row2.ScheduledAtUtc = DateTime.UtcNow.AddHours(-1);
        await _db.SaveChangesAsync();

        // Future important — should not appear
        var futureImportant = ValidRequest();
        futureImportant.Importance = MeetingImportance.Important;
        futureImportant.ScheduledAtUtc = DateTime.UtcNow.AddDays(3);
        await _svc.CreateAsync(_engagementId, Tenant, Actor, futureImportant);

        var result = await _svc.ListMissedForTenantAsync(Tenant);

        Assert.Single(result);
        Assert.Equal(pm.MeetingId, result[0].MeetingId);
    }
}