using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ConditionSatisfyTests : IDisposable
{
    private readonly WorkflowDbContext _db;
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly Mock<IClientActionService> _actions = new();
    private readonly ConditionService _svc;
    private readonly Guid _engId = Guid.NewGuid();
    private const string Tenant = "tenant-c26";
    private const string Actor = "staff@test";

    public ConditionSatisfyTests()
    {
        var opts = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase("cond-c26-" + Guid.NewGuid().ToString("N"))
            .Options;
        _db = new WorkflowDbContext(opts);

        _db.Engagements.Add(new Engagement
        {
            EngagementId = _engId,
            TenantId = Tenant,
            ClientId = "client-1",
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Verification,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        _svc = new ConditionService(_db, _actions.Object, _audit.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConditionService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private EngagementCondition SeedPayment(string status = ConditionStatus.Pending, bool active = true)
    {
        var c = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = _engId,
            TenantId = Tenant,
            Type = ConditionType.Payment,
            IsActive = active,
            Status = status,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Upfront deposit",
            Amount = 2500m,
            Currency = "GBP",
            PaymentType = ConditionPaymentType.Upfront,
            DueDateUtc = DateTime.UtcNow.AddDays(7),
            CreatedBy = Actor,
            CreatedAt = DateTime.UtcNow,
        };
        _db.EngagementConditions.Add(c);
        _db.SaveChanges();
        return c;
    }

    [Fact]
    public async Task Satisfy_PendingPayment_SetsSatisfiedAndPublishesPaymentStatusChanged()
    {
        var c = SeedPayment();

        var result = await _svc.SatisfyConditionAsync(_engId, c.ConditionId, Tenant, Actor);

        Assert.Equal(ConditionStatus.Satisfied, result.Status);
        Assert.NotNull(result.SatisfiedAt);
        Assert.Equal(Actor, result.SatisfiedBy);

        _audit.Verify(a => a.PublishEventAsync(
            _engId, Tenant, Actor, "PaymentStatusChanged", It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task Satisfy_AlreadySatisfied_IsIdempotentAndDoesNotPublish()
    {
        var c = SeedPayment(ConditionStatus.Satisfied);

        await _svc.SatisfyConditionAsync(_engId, c.ConditionId, Tenant, Actor);

        _audit.Verify(a => a.PublishEventAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    [Fact]
    public async Task Satisfy_DeactivatedCondition_Throws()
    {
        var c = SeedPayment(active: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.SatisfyConditionAsync(_engId, c.ConditionId, Tenant, Actor));
    }

    [Fact]
    public async Task Satisfy_RejectedCondition_Throws()
    {
        var c = SeedPayment(ConditionStatus.Rejected);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.SatisfyConditionAsync(_engId, c.ConditionId, Tenant, Actor));
    }

    [Fact]
    public async Task Satisfy_ClosedEngagement_Throws()
    {
        var c = SeedPayment();
        var eng = await _db.Engagements.FirstAsync(e => e.EngagementId == _engId);
        eng.Status = EngagementStatus.Closed;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.SatisfyConditionAsync(_engId, c.ConditionId, Tenant, Actor));
    }

    [Fact]
    public async Task Satisfy_CrossTenant_Throws()
    {
        var c = SeedPayment();
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _svc.SatisfyConditionAsync(_engId, c.ConditionId, "other-tenant", Actor));
    }

    [Fact]
    public async Task Satisfy_ApprovalCondition_PublishesConditionUpdated()
    {
        var c = SeedPayment();
        c.Type = ConditionType.Approval;
        await _db.SaveChangesAsync();

        await _svc.SatisfyConditionAsync(_engId, c.ConditionId, Tenant, Actor);

        _audit.Verify(a => a.PublishEventAsync(
            _engId, Tenant, Actor, "ConditionUpdated", It.IsAny<object>()),
            Times.Once);
    }
}