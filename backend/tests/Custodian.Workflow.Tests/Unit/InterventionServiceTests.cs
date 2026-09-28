using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Custodian.Workflow.Tests.Unit;

public class InterventionServiceTests : IDisposable
{
    private readonly WorkflowDbContext _db;
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly InterventionService _svc;
    private readonly Guid _engagementId = Guid.NewGuid();
    private const string Tenant = "tenant-test";
    private const string Actor = "staff@test";

    public InterventionServiceTests()
    {
        var opts = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase("interventions-" + Guid.NewGuid().ToString("N"))
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

        _svc = new InterventionService(_db, _audit.Object);
    }

    public void Dispose() => _db.Dispose();

    private static RecordInterventionRequest ValidRequest() => new()
    {
        Type = InterventionType.RecoveryAction,
        Reason = "Called the client; they will upload the MSA today.",
        Outcome = InterventionOutcome.Progressing,
    };

    [Fact]
    public async Task RecordAsync_ValidRequest_CreatesRowAndPublishesEvent()
    {
        var result = await _svc.RecordAsync(_engagementId, Tenant, Actor, ValidRequest());

        Assert.NotEqual(Guid.Empty, result.InterventionId);
        Assert.Equal(_engagementId, result.EngagementId);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(InterventionType.RecoveryAction, result.Type);
        Assert.Equal(InterventionOutcome.Progressing, result.Outcome);
        Assert.Equal(Actor, result.RecordedBy);

        Assert.Single(_db.Interventions);

        _audit.Verify(a => a.PublishEventAsync(
            _engagementId,
            Tenant,
            Actor,
            "intervention.recovered",
            It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task RecordAsync_EventCarriesWhatTheAuditTrailShows()
    {
        object? payload = null;
        _audit.Setup(a => a.PublishEventAsync(_engagementId, Tenant, Actor, "intervention.recovered", It.IsAny<object>()))
            .Callback<Guid, string, string, string, object>((_, _, _, _, p) => payload = p)
            .Returns(Task.CompletedTask);
        var blockerActionId = Guid.NewGuid();
        var req = ValidRequest();
        req.BlockerActionId = blockerActionId;

        var result = await _svc.RecordAsync(_engagementId, Tenant, Actor, req);

        var json = System.Text.Json.JsonSerializer.SerializeToElement(payload);
        Assert.Equal(result.InterventionId, json.GetProperty("interventionId").GetGuid());
        Assert.Equal("client-1", json.GetProperty("clientId").GetString());
        Assert.Equal(InterventionType.RecoveryAction, json.GetProperty("type").GetString());
        Assert.Equal(InterventionOutcome.Progressing, json.GetProperty("outcome").GetString());
        Assert.Equal(req.Reason, json.GetProperty("reason").GetString()); // staff-only; audit is staff-only
        Assert.Equal(blockerActionId, json.GetProperty("blockerActionId").GetGuid());
    }

    [Fact]
    public async Task RecordAsync_InvalidType_Throws()
    {
        var req = ValidRequest();
        req.Type = "NotARealType";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.RecordAsync(_engagementId, Tenant, Actor, req));

        Assert.Contains("Invalid intervention type", ex.Message);
        Assert.Empty(_db.Interventions);
    }

    [Fact]
    public async Task RecordAsync_InvalidOutcome_Throws()
    {
        var req = ValidRequest();
        req.Outcome = "NotARealOutcome";

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.RecordAsync(_engagementId, Tenant, Actor, req));

        Assert.Contains("Invalid outcome", ex.Message);
        Assert.Empty(_db.Interventions);
    }

    [Fact]
    public async Task RecordAsync_UnknownEngagement_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.RecordAsync(Guid.NewGuid(), Tenant, Actor, ValidRequest()));
    }

    [Fact]
    public async Task RecordAsync_CrossTenant_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.RecordAsync(_engagementId, "someone-elses-tenant", Actor, ValidRequest()));
    }

    [Fact]
    public async Task RecordAsync_TrimsReasonWhitespace()
    {
        var req = ValidRequest();
        req.Reason = "   spaced   ";

        var result = await _svc.RecordAsync(_engagementId, Tenant, Actor, req);

        Assert.Equal("spaced", result.Reason);
    }

    [Fact]
    public async Task ListForEngagementAsync_ReturnsNewestFirst()
    {
        await _svc.RecordAsync(_engagementId, Tenant, Actor, ValidRequest());

        // Simulate a later intervention by resetting the timestamp
        var first = _db.Interventions.Single();
        first.CreatedAt = DateTime.UtcNow.AddMinutes(-5);
        _db.SaveChanges();

        await _svc.RecordAsync(_engagementId, Tenant, Actor, ValidRequest());

        var rows = await _svc.ListForEngagementAsync(_engagementId, Tenant);

        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].CreatedAt >= rows[1].CreatedAt);
    }

    [Fact]
    public async Task ListForEngagementAsync_UnknownEngagement_ReturnsEmpty()
    {
        var rows = await _svc.ListForEngagementAsync(Guid.NewGuid(), Tenant);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task ListForEngagementAsync_TenantScoped_ReturnsOnlyMatchingTenant()
    {
        await _svc.RecordAsync(_engagementId, Tenant, Actor, ValidRequest());

        var other = await _svc.ListForEngagementAsync(_engagementId, "other-tenant");
        Assert.Empty(other);
    }
}