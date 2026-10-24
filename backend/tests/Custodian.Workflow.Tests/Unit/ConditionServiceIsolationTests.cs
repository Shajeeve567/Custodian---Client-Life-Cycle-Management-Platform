using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// ConditionService with "noise" in the database: conditions and tasks carrying this engagement id under
/// another tenant, and other engagements in the same tenant. Every lookup must ignore them, and every required argument is checked
/// on its own. Also covers the field-level rules of update and the decision events.
/// </summary>
public class ConditionServiceIsolationTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private static readonly Guid EngagementId = Guid.NewGuid();
    private static readonly Guid OtherEngagementId = Guid.NewGuid();

    private readonly WorkflowDbContext _db = new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseInMemoryDatabase("cond-iso-" + Guid.NewGuid().ToString("N")).Options);
    private readonly Mock<IClientActionService> _clientActions = new();
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly List<(string Type, object Payload)> _published = [];

    public ConditionServiceIsolationTests()
    {
        _audit.Setup(a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback((Guid _, string _, string _, string type, object payload) => _published.Add((type, payload)))
            .Returns(Task.CompletedTask);
        _clientActions.Setup(s => s.ClientOwnsEngagementAsync(EngagementId, Tenant, "client-1")).ReturnsAsync(true);

        _db.Engagements.AddRange(
            new Engagement { EngagementId = EngagementId, TenantId = Tenant, ClientId = "client-1", Status = EngagementStatus.Started, Stage = EngagementStage.Onboarding },
            new Engagement { EngagementId = OtherEngagementId, TenantId = Tenant, ClientId = "client-2", Status = EngagementStatus.Started });
        _db.SaveChanges();
    }

    private ConditionService Service => new(_db, _clientActions.Object, _audit.Object, NullLogger<ConditionService>.Instance);

    private EngagementCondition Seed(
        Guid? engagementId = null, string tenant = Tenant, string type = ConditionType.Approval, string status = ConditionStatus.Pending,
        bool active = true, EngagementStage before = EngagementStage.Execution, DateTime? createdAt = null, string title = "Sign-off")
    {
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = engagementId ?? EngagementId,
            TenantId = tenant,
            Type = type,
            Status = status,
            IsActive = active,
            RequiredBeforeStage = before,
            Title = title,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Amount = type == ConditionType.Payment ? 100m : null,
            Currency = type == ConditionType.Payment ? "USD" : null,
            PaymentType = type == ConditionType.Payment ? ConditionPaymentType.Milestone : null
        };
        _db.EngagementConditions.Add(condition);
        _db.SaveChanges();
        return condition;
    }

    /// <summary>The same condition id must never be reachable from another tenant or engagement.</summary>
    private void SeedNoise()
    {
        Seed(tenant: OtherTenant, title: "other tenant");
        Seed(engagementId: OtherEngagementId, title: "other engagement");
    }

    private static object? Prop(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source);

    // ==========================================
    // Reads
    // ==========================================

    [Fact]
    public async Task GetStaff_ReturnsOnlyThisEngagementInThisTenant_OrderedByStageThenCreation()
    {
        SeedNoise();
        var late = Seed(before: EngagementStage.Closure, createdAt: DateTime.UtcNow.AddDays(-5), title: "late");
        var secondEarly = Seed(before: EngagementStage.Verification, createdAt: DateTime.UtcNow.AddDays(-1), title: "early-2");
        var firstEarly = Seed(before: EngagementStage.Verification, createdAt: DateTime.UtcNow.AddDays(-2), title: "early-1", active: false);

        var result = (await Service.GetConditionsStaffAsync(EngagementId, Tenant)).ToList();

        Assert.Equal(["early-1", "early-2", "late"], result.Select(c => c.Title));
        Assert.All(result, c => Assert.Equal("client-1", c.TargetClientId));
    }

    [Fact]
    public async Task GetStaff_CanExcludeInactive()
    {
        Seed(title: "active");
        Seed(type: ConditionType.Payment, active: false, title: "inactive");

        Assert.Equal(["active"], (await Service.GetConditionsStaffAsync(EngagementId, Tenant, includeInactive: false)).Select(c => c.Title));
        Assert.Equal(2, (await Service.GetConditionsStaffAsync(EngagementId, Tenant, includeInactive: true)).Count());
    }

    [Fact]
    public async Task GetStaff_InvalidArgumentsOrNothingFound_AreEmpty()
    {
        Seed();

        Assert.Empty(await Service.GetConditionsStaffAsync(EngagementId, " "));
        Assert.Empty(await Service.GetConditionsStaffAsync(Guid.Empty, Tenant));
        Assert.Empty(await Service.GetConditionsStaffAsync(Guid.NewGuid(), Tenant));
    }

    [Fact]
    public async Task GetClient_ReturnsActiveConditionsOfOwnEngagementOnly_InOrder()
    {
        SeedNoise();
        Seed(before: EngagementStage.Closure, title: "later");
        Seed(before: EngagementStage.Verification, title: "sooner");
        Seed(type: ConditionType.Payment, active: false, title: "inactive");

        var result = (await Service.GetConditionsClientAsync(EngagementId, Tenant, "client-1")).ToList();

        Assert.Equal(["sooner", "later"], result.Select(c => c.Title));
    }

    [Fact]
    public async Task GetClient_NotOwnerOrMissingArguments_AreEmpty()
    {
        Seed();

        Assert.Empty(await Service.GetConditionsClientAsync(EngagementId, Tenant, "client-2"));
        Assert.Empty(await Service.GetConditionsClientAsync(EngagementId, " ", "client-1"));
        Assert.Empty(await Service.GetConditionsClientAsync(Guid.Empty, Tenant, "client-1"));
        Assert.Empty(await Service.GetConditionsClientAsync(EngagementId, Tenant, " "));
    }

    [Fact]
    public async Task GetByIdStaff_FindsOnlyInItsEngagementAndTenant()
    {
        SeedNoise();
        var condition = Seed();

        Assert.Equal("client-1", (await Service.GetConditionByIdStaffAsync(EngagementId, condition.ConditionId, Tenant))!.TargetClientId);
        Assert.Null(await Service.GetConditionByIdStaffAsync(EngagementId, condition.ConditionId, OtherTenant));
        Assert.Null(await Service.GetConditionByIdStaffAsync(OtherEngagementId, condition.ConditionId, Tenant));
        Assert.Null(await Service.GetConditionByIdStaffAsync(EngagementId, Guid.NewGuid(), Tenant));
        Assert.Null(await Service.GetConditionByIdStaffAsync(EngagementId, condition.ConditionId, " "));
        Assert.Null(await Service.GetConditionByIdStaffAsync(Guid.Empty, condition.ConditionId, Tenant));
        Assert.Null(await Service.GetConditionByIdStaffAsync(EngagementId, Guid.Empty, Tenant));
    }

    [Fact]
    public async Task GetByIdClient_FindsOnlyActiveConditionsOfOwnEngagement()
    {
        SeedNoise();
        var condition = Seed();
        var inactive = Seed(type: ConditionType.Payment, active: false);

        Assert.Equal(condition.ConditionId, (await Service.GetConditionByIdClientAsync(EngagementId, condition.ConditionId, Tenant, "client-1"))!.ConditionId);
        Assert.Null(await Service.GetConditionByIdClientAsync(EngagementId, inactive.ConditionId, Tenant, "client-1"));
        Assert.Null(await Service.GetConditionByIdClientAsync(EngagementId, Guid.NewGuid(), Tenant, "client-1"));
        Assert.Null(await Service.GetConditionByIdClientAsync(EngagementId, condition.ConditionId, Tenant, "client-2"));
        Assert.Null(await Service.GetConditionByIdClientAsync(EngagementId, condition.ConditionId, " ", "client-1"));
        Assert.Null(await Service.GetConditionByIdClientAsync(Guid.Empty, condition.ConditionId, Tenant, "client-1"));
        Assert.Null(await Service.GetConditionByIdClientAsync(EngagementId, Guid.Empty, Tenant, "client-1"));
        Assert.Null(await Service.GetConditionByIdClientAsync(EngagementId, condition.ConditionId, Tenant, " "));
    }

    // ==========================================
    // Overdue flag
    // ==========================================

    [Theory]
    [InlineData(-1, ConditionStatus.Pending, true)]
    [InlineData(-1, ConditionStatus.Satisfied, false)]
    [InlineData(1, ConditionStatus.Pending, false)]
    [InlineData(null, ConditionStatus.Pending, false)]
    public async Task IsOverdue_OnlyForPendingConditionsPastTheirDueDate(int? dueInDays, string status, bool expected)
    {
        var condition = Seed(status: status);
        condition.DueDateUtc = dueInDays is null ? null : DateTime.UtcNow.AddDays(dueInDays.Value);
        _db.SaveChanges();

        Assert.Equal(expected, (await Service.GetConditionByIdStaffAsync(EngagementId, condition.ConditionId, Tenant))!.IsOverdue);
        Assert.Equal(expected, (await Service.GetConditionByIdClientAsync(EngagementId, condition.ConditionId, Tenant, "client-1"))!.IsOverdue);
    }

    // ==========================================
    // Attach
    // ==========================================

    private static AttachConditionDto Attach(string type = ConditionType.Approval) => new()
    {
        Type = type,
        Title = "  Scope sign-off ",
        RequiredBeforeStage = EngagementStage.Execution,
        Amount = type == ConditionType.Payment ? 250m : null,
        Currency = type == ConditionType.Payment ? " lkr " : null
    };

    [Theory]
    [InlineData(" ", false)]
    [InlineData(Tenant, true)]
    public async Task Attach_RequiresTenantAndEngagement(string tenant, bool emptyEngagement)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.AttachConditionAsync(emptyEngagement ? Guid.Empty : EngagementId, tenant, Attach(), "staff-1"));

        Assert.StartsWith("TenantId and EngagementId are required.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Discount")]
    public async Task Attach_UnknownType_IsRejected(string type)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.AttachConditionAsync(EngagementId, Tenant, Attach(type), "staff-1"));

        Assert.StartsWith($"Invalid condition type '{type}'.", error.Message);
    }

    [Fact]
    public async Task Attach_TypeIsTrimmedAndCanonical()
    {
        var dto = Attach();
        dto.Type = " approval ";

        var result = await Service.AttachConditionAsync(EngagementId, Tenant, dto, "staff-1");

        Assert.Equal(ConditionType.Approval, result.Type);
        Assert.Equal("Scope sign-off", result.Title);
    }

    [Fact]
    public async Task Attach_EngagementOnlyInAnotherTenant_IsNotFound()
    {
        var foreign = Guid.NewGuid();
        _db.Engagements.Add(new Engagement { EngagementId = foreign, TenantId = OtherTenant, Status = EngagementStatus.Started });
        _db.SaveChanges();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.AttachConditionAsync(foreign, Tenant, Attach(), "staff-1"));

        Assert.Equal($"Engagement '{foreign}' was not found in tenant '{Tenant}'.", error.Message);
    }

    [Fact]
    public async Task Attach_DuplicateCheck_IgnoresOtherEngagementsTenantsTypesAndInactive()
    {
        Seed(engagementId: OtherEngagementId);
        Seed(tenant: OtherTenant);
        Seed(type: ConditionType.Payment);
        Seed(active: false);

        var result = await Service.AttachConditionAsync(EngagementId, Tenant, Attach(), "staff-1");

        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task Attach_Payment_NormalisesCurrency_AndPaymentType()
    {
        var dto = Attach(ConditionType.Payment);
        dto.PaymentType = " upfront ";

        var result = await Service.AttachConditionAsync(EngagementId, Tenant, dto, "staff-1");

        Assert.Equal(250m, result.Amount);
        Assert.Equal("LKR", result.Currency);
        Assert.Equal(ConditionPaymentType.Upfront, result.PaymentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Attach_Payment_WithoutPaymentType_DefaultsToMilestone(string? paymentType)
    {
        var dto = Attach(ConditionType.Payment);
        dto.PaymentType = paymentType;

        Assert.Equal(ConditionPaymentType.Milestone, (await Service.AttachConditionAsync(EngagementId, Tenant, dto, "staff-1")).PaymentType);
    }

    [Fact]
    public async Task Attach_Payment_UnknownPaymentType_IsRejected()
    {
        var dto = Attach(ConditionType.Payment);
        dto.PaymentType = "Instalment";

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.AttachConditionAsync(EngagementId, Tenant, dto, "staff-1"));

        Assert.StartsWith("Invalid payment type 'Instalment'.", error.Message);
    }

    // ==========================================
    // Update
    // ==========================================

    [Theory]
    [InlineData(" ", false, false)]
    [InlineData(Tenant, true, false)]
    [InlineData(Tenant, false, true)]
    public async Task Update_Deactivate_Satisfy_RequireEveryId(string tenant, bool emptyEngagement, bool emptyCondition)
    {
        var condition = Seed();
        var engagementId = emptyEngagement ? Guid.Empty : EngagementId;
        var conditionId = emptyCondition ? Guid.Empty : condition.ConditionId;
        const string message = "TenantId, EngagementId, and ConditionId are required.";

        Assert.Equal(message, (await Assert.ThrowsAsync<ArgumentException>(() => Service.UpdateConditionAsync(engagementId, conditionId, tenant, new UpdateConditionDto(), "s"))).Message);
        Assert.Equal(message, (await Assert.ThrowsAsync<ArgumentException>(() => Service.DeactivateConditionAsync(engagementId, conditionId, tenant, new DeactivateConditionDto { Reason = "r" }, "s"))).Message);
        Assert.Equal(message, (await Assert.ThrowsAsync<ArgumentException>(() => Service.SatisfyConditionAsync(engagementId, conditionId, tenant, "s"))).Message);
    }

    [Fact]
    public async Task Update_Deactivate_Satisfy_DoNotReachAcrossEngagementsOrTenants()
    {
        var condition = Seed();
        Seed(engagementId: OtherEngagementId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.UpdateConditionAsync(OtherEngagementId, condition.ConditionId, Tenant, new UpdateConditionDto(), "s"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.UpdateConditionAsync(EngagementId, condition.ConditionId, OtherTenant, new UpdateConditionDto(), "s"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.DeactivateConditionAsync(OtherEngagementId, condition.ConditionId, Tenant, new DeactivateConditionDto { Reason = "r" }, "s"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.SatisfyConditionAsync(OtherEngagementId, condition.ConditionId, Tenant, "s"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.SatisfyConditionAsync(EngagementId, Guid.NewGuid(), Tenant, "s"));
        Assert.Equal(ConditionStatus.Pending, (await _db.EngagementConditions.FindAsync(condition.ConditionId))!.Status);
    }

    [Fact]
    public async Task Update_TextFields_AreTrimmed_AndBlankClearsThem()
    {
        var condition = Seed();

        var trimmed = await Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant,
            new UpdateConditionDto { Title = " New title ", Description = " New description ", InternalNote = " Note " }, "staff-1");
        Assert.Equal(("New title", "New description", "Note"), (trimmed.Title, trimmed.Description, trimmed.InternalNote));

        var cleared = await Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant,
            new UpdateConditionDto { Description = "  ", InternalNote = "" }, "staff-1");
        Assert.Null(cleared.Description);
        Assert.Null(cleared.InternalNote);
        Assert.Equal("New title", cleared.Title);
    }

    [Fact]
    public async Task Update_OmittedFields_AreKept()
    {
        var condition = Seed();
        var due = DateTime.UtcNow.AddDays(3);
        await Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant,
            new UpdateConditionDto { Description = "Keep me", InternalNote = "Keep note", DueDateUtc = due }, "staff-1");

        var result = await Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant, new UpdateConditionDto(), "staff-2");

        Assert.Equal(("Keep me", "Keep note", due), (result.Description, result.InternalNote, result.DueDateUtc!.Value));
        Assert.Equal("staff-2", result.UpdatedBy);
    }

    [Fact]
    public async Task Update_IsSaved_AndAudited()
    {
        var condition = Seed(type: ConditionType.Payment);
        var due = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        await Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant,
            new UpdateConditionDto { Title = "Deposit", DueDateUtc = due, Amount = 0.01m, Currency = " eur ", PaymentType = "FINAL" }, "staff-1");

        _db.ChangeTracker.Clear();
        var stored = await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal(("Deposit", due, 0.01m, "EUR", ConditionPaymentType.Final, "staff-1"),
            (stored.Title, stored.DueDateUtc!.Value, stored.Amount!.Value, stored.Currency, stored.PaymentType, stored.UpdatedBy));
        Assert.NotNull(stored.UpdatedAt);

        var (type, payload) = Assert.Single(_published);
        Assert.Equal("ConditionUpdated", type);
        Assert.Equal(condition.ConditionId, Prop(payload, "conditionId"));
        Assert.Equal("Deposit", Prop(payload, "title"));
        Assert.Equal(0.01m, Prop(payload, "amount"));
        Assert.Equal("EUR", Prop(payload, "currency"));
        Assert.Equal(ConditionPaymentType.Final, Prop(payload, "paymentType"));
        Assert.Equal("staff-1", Prop(payload, "updatedBy"));
    }

    [Theory]
    [InlineData(0, "Amount must be greater than 0.")]
    [InlineData(-5, "Amount must be greater than 0.")]
    public async Task Update_Payment_NonPositiveAmount_IsRejected(int amount, string message)
    {
        var condition = Seed(type: ConditionType.Payment);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant, new UpdateConditionDto { Amount = amount }, "s"));

        Assert.StartsWith(message, error.Message);
    }

    [Fact]
    public async Task Update_Payment_BadCurrencyOrPaymentType_IsRejected()
    {
        var condition = Seed(type: ConditionType.Payment);

        var currency = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant, new UpdateConditionDto { Currency = "EURO" }, "s"));
        var paymentType = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant, new UpdateConditionDto { PaymentType = "Weekly" }, "s"));

        Assert.StartsWith("Currency must be a valid 3-letter ISO currency code.", currency.Message);
        Assert.StartsWith("Invalid payment type 'Weekly'.", paymentType.Message);
    }

    [Fact]
    public async Task Update_Approval_IgnoresPaymentFields()
    {
        var condition = Seed();

        var result = await Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant,
            new UpdateConditionDto { Amount = -1, Currency = "nope", PaymentType = "nope" }, "s");

        Assert.Null(result.Amount);
        Assert.Null(result.Currency);
        Assert.Null(result.PaymentType);
    }

    [Fact]
    public async Task Update_BlankTitle_IsRejected()
    {
        var condition = Seed();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.UpdateConditionAsync(EngagementId, condition.ConditionId, Tenant, new UpdateConditionDto { Title = " " }, "s"));

        Assert.StartsWith("Title cannot be empty.", error.Message);
    }

    [Fact]
    public async Task Satisfy_IsSaved()
    {
        var condition = Seed();

        await Service.SatisfyConditionAsync(EngagementId, condition.ConditionId, Tenant, "staff-1");

        _db.ChangeTracker.Clear();
        var stored = await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal((ConditionStatus.Satisfied, "staff-1", "staff-1"), (stored.Status, stored.SatisfiedBy, stored.UpdatedBy));
        Assert.NotNull(stored.SatisfiedAt);
    }

    [Fact]
    public async Task Satisfy_EngagementOnlyInAnotherTenant_IsNotFound()
    {
        var foreign = Guid.NewGuid();
        _db.Engagements.Add(new Engagement { EngagementId = foreign, TenantId = OtherTenant, Status = EngagementStatus.Started });
        _db.SaveChanges();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.SatisfyConditionAsync(foreign, Guid.NewGuid(), Tenant, "s"));

        Assert.Equal($"Engagement '{foreign}' was not found in tenant '{Tenant}'.", error.Message);
    }

    // ==========================================
    // SetConditionStatusAsync
    // ==========================================

    [Theory]
    [InlineData(" ", false)]
    [InlineData(Tenant, true)]
    public async Task SetStatus_RequiresTenantAndCondition(string tenant, bool emptyCondition)
    {
        var condition = Seed();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.SetConditionStatusAsync(emptyCondition ? Guid.Empty : condition.ConditionId, tenant, ConditionStatus.Satisfied, "s"));

        Assert.Equal("TenantId and ConditionId are required.", error.Message);
    }

    [Fact]
    public async Task SetStatus_ConditionInAnotherTenant_IsNotFound()
    {
        var condition = Seed(tenant: OtherTenant);
        Seed();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.SetConditionStatusAsync(condition.ConditionId, Tenant, ConditionStatus.Satisfied, "s"));

        Assert.Equal($"Condition '{condition.ConditionId}' was not found.", error.Message);
    }

    [Fact]
    public async Task SetStatus_DeactivatedCondition_IsRefused()
    {
        var condition = Seed(active: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service.SetConditionStatusAsync(condition.ConditionId, Tenant, ConditionStatus.Satisfied, "s"));

        Assert.Equal($"Cannot update status of deactivated condition '{condition.ConditionId}'.", error.Message);
    }

    [Fact]
    public async Task SetStatus_PendingToSatisfied_StampsWhoAndWhen_AndIsAudited()
    {
        var condition = Seed();

        await Service.SetConditionStatusAsync(condition.ConditionId, Tenant, ConditionStatus.Satisfied, "staff-1", "Paid in full");

        _db.ChangeTracker.Clear();
        var stored = await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal((ConditionStatus.Satisfied, "staff-1", "staff-1"), (stored.Status, stored.SatisfiedBy, stored.UpdatedBy));
        Assert.NotNull(stored.SatisfiedAt);
        var (type, payload) = Assert.Single(_published);
        Assert.Equal("ConditionUpdated", type);
        Assert.Equal(ConditionStatus.Satisfied, Prop(payload, "status"));
        Assert.Equal("Paid in full", Prop(payload, "statusReason"));
        _audit.Verify(a => a.PublishEventAsync(EngagementId, Tenant, "staff-1", "ConditionUpdated", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task SetStatus_PendingToRejected_DoesNotMarkSatisfied()
    {
        var condition = Seed();

        await Service.SetConditionStatusAsync(condition.ConditionId, Tenant, ConditionStatus.Rejected, "staff-1");

        var stored = await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal(ConditionStatus.Rejected, stored.Status);
        Assert.Null(stored.SatisfiedBy);
        Assert.Null(stored.SatisfiedAt);
    }

    [Fact]
    public async Task SetStatus_RejectedBackToPending_ClearsSatisfaction()
    {
        var condition = Seed(status: ConditionStatus.Rejected);
        condition.SatisfiedBy = "earlier";
        condition.SatisfiedAt = DateTime.UtcNow;
        _db.SaveChanges();

        await Service.SetConditionStatusAsync(condition.ConditionId, Tenant, ConditionStatus.Pending, "staff-1");

        _db.ChangeTracker.Clear();
        var stored = await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal(ConditionStatus.Pending, stored.Status);
        Assert.Null(stored.SatisfiedBy);
        Assert.Null(stored.SatisfiedAt);
    }

    [Theory]
    [InlineData(ConditionStatus.Satisfied, ConditionStatus.Pending)]
    [InlineData(ConditionStatus.Satisfied, ConditionStatus.Rejected)]
    [InlineData(ConditionStatus.Rejected, ConditionStatus.Satisfied)]
    [InlineData(ConditionStatus.Pending, ConditionStatus.Pending)]
    [InlineData(ConditionStatus.Rejected, ConditionStatus.Rejected)]
    public async Task SetStatus_OtherTransitions_AreRefused(string from, string to)
    {
        var condition = Seed(status: from);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service.SetConditionStatusAsync(condition.ConditionId, Tenant, to, "s"));

        Assert.Equal($"Invalid condition status transition from '{from}' to '{to}'.", error.Message);
        Assert.Empty(_published);
    }

    // ==========================================
    // Client decisions
    // ==========================================

    [Theory]
    [InlineData(" ", false, false, "client-1")]
    [InlineData(Tenant, true, false, "client-1")]
    [InlineData(Tenant, false, true, "client-1")]
    [InlineData(Tenant, false, false, " ")]
    public async Task Decisions_RequireEveryArgument(string tenant, bool emptyEngagement, bool emptyCondition, string client)
    {
        var condition = Seed();
        var engagementId = emptyEngagement ? Guid.Empty : EngagementId;
        var conditionId = emptyCondition ? Guid.Empty : condition.ConditionId;
        const string message = "TenantId, EngagementId, ConditionId, and CallerClientId are required.";

        Assert.Equal(message, (await Assert.ThrowsAsync<ArgumentException>(() => Service.ApproveConditionAsync(engagementId, conditionId, tenant, client, "a"))).Message);
        Assert.Equal(message, (await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.RejectConditionAsync(engagementId, conditionId, tenant, client, new RejectApprovalDto { Reason = "No" }, "a"))).Message);
    }

    [Fact]
    public async Task Decisions_EngagementOnlyInAnotherTenant_IsNotFound()
    {
        var foreign = Guid.NewGuid();
        _db.Engagements.Add(new Engagement { EngagementId = foreign, TenantId = OtherTenant, ClientId = "client-1", Status = EngagementStatus.Started });
        _db.SaveChanges();

        Assert.Equal($"Engagement '{foreign}' was not found.",
            (await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.ApproveConditionAsync(foreign, Guid.NewGuid(), Tenant, "client-1", "a"))).Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Service.RejectConditionAsync(foreign, Guid.NewGuid(), Tenant, "client-1", new RejectApprovalDto { Reason = "No" }, "a"));
    }

    [Fact]
    public async Task Decisions_ConditionOfAnotherEngagement_IsNotFound()
    {
        var foreign = Seed(engagementId: OtherEngagementId);

        Assert.Equal($"Condition '{foreign.ConditionId}' was not found for engagement '{EngagementId}'.",
            (await Assert.ThrowsAsync<KeyNotFoundException>(() => Service.ApproveConditionAsync(EngagementId, foreign.ConditionId, Tenant, "client-1", "a"))).Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Service.RejectConditionAsync(EngagementId, foreign.ConditionId, Tenant, "client-1", new RejectApprovalDto { Reason = "No" }, "a"));
    }

    private ClientAction SeedLinkedAction(EngagementCondition condition, Guid? engagementId = null, string tenant = Tenant)
    {
        var action = new ClientAction
        {
            EngagementId = engagementId ?? EngagementId,
            TenantId = tenant,
            Title = "Approve scope",
            Type = ClientActionType.Approval,
            Status = ClientActionStatus.Pending,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = condition.ConditionId
        };
        _db.ClientActions.Add(action);
        _db.SaveChanges();
        return action;
    }

    [Fact]
    public async Task Approve_CompletesOnlyThisEngagementsLinkedTasks_AndPublishesApprovalCompleted()
    {
        var condition = Seed();
        var linked = SeedLinkedAction(condition);
        var otherTenant = SeedLinkedAction(condition, tenant: OtherTenant);
        var otherEngagement = SeedLinkedAction(condition, engagementId: OtherEngagementId);

        await Service.ApproveConditionAsync(EngagementId, condition.ConditionId, Tenant, "CLIENT-1", "user-1");

        _db.ChangeTracker.Clear();
        var statuses = await _db.ClientActions.ToDictionaryAsync(a => a.ActionId, a => a.Status);
        Assert.Equal(ClientActionStatus.Completed, statuses[linked.ActionId]);
        Assert.Equal(ClientActionStatus.Pending, statuses[otherTenant.ActionId]);
        Assert.Equal(ClientActionStatus.Pending, statuses[otherEngagement.ActionId]);
        Assert.Equal(ConditionStatus.Satisfied, (await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId)).Status);

        var (_, approved) = Assert.Single(_published, p => p.Type == "ApprovalCompleted");
        Assert.Equal(("Approval", "Satisfied", "Approved", "client-1"),
            ((string)Prop(approved, "type")!, (string)Prop(approved, "status")!, (string)Prop(approved, "decision")!, (string)Prop(approved, "clientId")!));
        Assert.Single(_published, p => p.Type == "ClientActionStatusChanged");
    }

    [Fact]
    public async Task Reject_RejectsOnlyThisEngagementsLinkedTasks_AndPublishesApprovalRejected()
    {
        var condition = Seed();
        var linked = SeedLinkedAction(condition);
        var otherEngagement = SeedLinkedAction(condition, engagementId: OtherEngagementId);

        await Service.RejectConditionAsync(EngagementId, condition.ConditionId, Tenant, "client-1", new RejectApprovalDto { Reason = "  Too expensive " }, "user-1");

        _db.ChangeTracker.Clear();
        Assert.Equal(ClientActionStatus.Rejected, (await _db.ClientActions.SingleAsync(a => a.ActionId == linked.ActionId)).Status);
        Assert.Equal(ClientActionStatus.Pending, (await _db.ClientActions.SingleAsync(a => a.ActionId == otherEngagement.ActionId)).Status);
        var stored = await _db.EngagementConditions.SingleAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal((ConditionStatus.Rejected, "Too expensive"), (stored.Status, stored.RejectionReason));

        var (_, rejected) = Assert.Single(_published, p => p.Type == "ApprovalRejected");
        Assert.Equal(("Approval", "Rejected", "Rejected", "Too expensive"),
            ((string)Prop(rejected, "type")!, (string)Prop(rejected, "status")!, (string)Prop(rejected, "decision")!, (string)Prop(rejected, "rejectionReason")!));
    }

    [Fact]
    public async Task Reject_ReasonOfExactly500Chars_IsAccepted_501IsNot()
    {
        var condition = Seed();

        await Service.RejectConditionAsync(EngagementId, condition.ConditionId, Tenant, "client-1", new RejectApprovalDto { Reason = " " + new string('r', 500) + " " }, "a");
        var tooLong = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.RejectConditionAsync(EngagementId, condition.ConditionId, Tenant, "client-1", new RejectApprovalDto { Reason = new string('r', 501) }, "a"));

        Assert.StartsWith("Rejection reason cannot exceed 500 characters.", tooLong.Message);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed, "closed")]
    [InlineData(EngagementStatus.Cancelled, "cancelled")]
    public async Task Decisions_OnTerminalEngagement_AreRefused(EngagementStatus status, string word)
    {
        var condition = Seed();
        var engagement = await _db.Engagements.SingleAsync(e => e.EngagementId == EngagementId && e.TenantId == Tenant);
        engagement.Status = status;
        _db.SaveChanges();

        Assert.Equal($"Cannot decide approval condition on {word} engagement.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() => Service.ApproveConditionAsync(EngagementId, condition.ConditionId, Tenant, "client-1", "a"))).Message);
        Assert.Equal($"Cannot decide approval condition on {word} engagement.",
            (await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service.RejectConditionAsync(EngagementId, condition.ConditionId, Tenant, "client-1", new RejectApprovalDto { Reason = "No" }, "a"))).Message);
    }

    [Fact]
    public void DeriveApprovalStatus_MapsApprovalStatuses_AndIgnoresPayments()
    {
        Assert.Equal("Pending", ConditionService.DeriveApprovalStatus("approval", "pending"));
        Assert.Equal("Approved", ConditionService.DeriveApprovalStatus(ConditionType.Approval, ConditionStatus.Satisfied));
        Assert.Equal("Rejected", ConditionService.DeriveApprovalStatus(ConditionType.Approval, "REJECTED"));
        Assert.Equal("Archived", ConditionService.DeriveApprovalStatus(ConditionType.Approval, "Archived"));
        Assert.Null(ConditionService.DeriveApprovalStatus(ConditionType.Payment, ConditionStatus.Pending));
    }
}
