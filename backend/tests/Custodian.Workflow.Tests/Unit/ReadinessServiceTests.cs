using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ReadinessServiceTests : IDisposable
{
    private readonly WorkflowDbContext _db;
    private readonly Mock<IGateEvaluator> _gate = new();
    private readonly ReadinessService _svc;
    private readonly Guid _engId = Guid.NewGuid();
    private const string Tenant = "tenant-readiness";

    public ReadinessServiceTests()
    {
        var opts = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase("readiness-" + Guid.NewGuid().ToString("N"))
            .Options;
        _db = new WorkflowDbContext(opts);
        _svc = new ReadinessService(_db, _gate.Object);
    }

    public void Dispose() => _db.Dispose();

    private Engagement Seed(EngagementStatus status, EngagementStage stage)
    {
        var e = new Engagement
        {
            EngagementId = _engId,
            TenantId = Tenant,
            ClientId = "c1",
            StaffId = "s1",
            Status = status,
            Stage = stage,
            CreatedAt = DateTime.UtcNow,
        };
        _db.Engagements.Add(e);
        _db.SaveChanges();
        return e;
    }

    [Fact]
    public async Task UnknownEngagement_Throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.EvaluateAsync(Guid.NewGuid(), Tenant));
    }

    [Fact]
    public async Task CrossTenant_Throws()
    {
        Seed(EngagementStatus.Started, EngagementStage.Closure);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.EvaluateAsync(_engId, "other-tenant"));
    }

    [Fact]
    public async Task ClosedEngagement_ReturnsAlreadyClosed_NoGateCall()
    {
        Seed(EngagementStatus.Closed, EngagementStage.Closure);
        var r = await _svc.EvaluateAsync(_engId, Tenant);
        Assert.False(r.IsReady);
        Assert.Equal("AlreadyClosed", r.State);
        Assert.Empty(r.Blockers);

        _gate.Verify(g => g.EvaluateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelledEngagement_ReturnsAlreadyCancelled()
    {
        Seed(EngagementStatus.Cancelled, EngagementStage.Onboarding);
        var r = await _svc.EvaluateAsync(_engId, Tenant);
        Assert.False(r.IsReady);
        Assert.Equal("AlreadyCancelled", r.State);
    }

    [Fact]
    public async Task DraftEngagement_ReturnsNotStarted_NoGateCall()
    {
        Seed(EngagementStatus.Draft, EngagementStage.Onboarding);
        var r = await _svc.EvaluateAsync(_engId, Tenant);
        Assert.False(r.IsReady);
        Assert.Equal("NotStarted", r.State);
        Assert.Single(r.Blockers);

        _gate.Verify(g => g.EvaluateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartedButNotAtClosure_ReturnsNotAtClosure_NoGateCall()
    {
        Seed(EngagementStatus.Started, EngagementStage.Verification);
        var r = await _svc.EvaluateAsync(_engId, Tenant);
        Assert.False(r.IsReady);
        Assert.Equal("NotAtClosure", r.State);
        Assert.Single(r.Blockers);
        Assert.Equal("Stage", r.Blockers[0].Kind);

        _gate.Verify(g => g.EvaluateAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StartedAtClosure_GateSatisfied_ReturnsReady()
    {
        Seed(EngagementStatus.Started, EngagementStage.Closure);
        _gate.Setup(g => g.EvaluateAsync(_engId, Tenant, EngagementStage.Closure, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied(Array.Empty<GateRequirementResult>()));

        var r = await _svc.EvaluateAsync(_engId, Tenant);

        Assert.True(r.IsReady);
        Assert.Equal("Ready", r.State);
        Assert.Empty(r.Blockers);
    }

    [Fact]
    public async Task StartedAtClosure_GateBlocked_ReturnsClassifiedBlockers()
    {
        Seed(EngagementStatus.Started, EngagementStage.Closure);
        _gate.Setup(g => g.EvaluateAsync(_engId, Tenant, EngagementStage.Closure, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Blocked(
                "blocked",
                new[]
                {
                    new GateRequirementResult("Doc X", false, "Required document 'Doc X' is missing."),
                    new GateRequirementResult("Payment", false, "Payment condition 'Deposit' is not satisfied."),
                    new GateRequirementResult("Task Y", false, "Task 'Task Y' is still Pending."),
                    new GateRequirementResult("Req Z", false, "Required information 'Req Z' has not been approved."),
                    new GateRequirementResult("Other", false, "Something else blocks."),
                }));

        var r = await _svc.EvaluateAsync(_engId, Tenant);

        Assert.False(r.IsReady);
        Assert.Equal("NotReady", r.State);
        Assert.Equal(5, r.Blockers.Count);
        Assert.Equal("Document", r.Blockers[0].Kind);
        Assert.Equal("Condition", r.Blockers[1].Kind);
        Assert.Equal("Task", r.Blockers[2].Kind);
        Assert.Equal("Requirement", r.Blockers[3].Kind);
        Assert.Equal("Gate", r.Blockers[4].Kind);
    }

    [Fact]
    public async Task StartedAtClosure_GateBlockedWithNoRequirements_SurfacesExternalBlocker()
    {
        Seed(EngagementStatus.Started, EngagementStage.Closure);
        _gate.Setup(g => g.EvaluateAsync(_engId, Tenant, EngagementStage.Closure, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Blocked(
                "Unable to verify required documents right now because the Documents service is unavailable.",
                Array.Empty<GateRequirementResult>()));

        var r = await _svc.EvaluateAsync(_engId, Tenant);

        Assert.False(r.IsReady);
        Assert.Single(r.Blockers);
        Assert.Equal("External", r.Blockers[0].Kind);
    }
}