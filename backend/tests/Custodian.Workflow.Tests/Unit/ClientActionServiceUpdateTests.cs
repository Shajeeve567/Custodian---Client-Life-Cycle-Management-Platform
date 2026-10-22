using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// ClientActionService: the standard checklist's content, and what UpdateActionAsync records in the
/// ClientActionUpdated audit event (each changed field with its before and after value).
/// </summary>
public class ClientActionServiceUpdateTests
{
    private const string Tenant = "tenant-001";
    private static readonly Guid EngagementId = Guid.NewGuid();

    private readonly WorkflowDbContext _db = new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseInMemoryDatabase("cas-update-" + Guid.NewGuid().ToString("N")).Options);
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly List<(string Type, object Payload)> _published = [];

    public ClientActionServiceUpdateTests()
    {
        _audit.Setup(a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback((Guid _, string _, string _, string type, object payload) => _published.Add((type, payload)))
            .Returns(Task.CompletedTask);
    }

    private ClientActionService Service => new(_db, _audit.Object);

    private async Task<ClientAction> SeedTaskAsync(
        Action<ClientAction>? configure = null,
        EngagementStatus status = EngagementStatus.Started,
        EngagementStage stage = EngagementStage.Onboarding)
    {
        _db.Engagements.Add(new Engagement { EngagementId = EngagementId, TenantId = Tenant, ClientId = "client-1", StaffId = "staff-1", Status = status, Stage = stage });
        var task = new ClientAction
        {
            EngagementId = EngagementId,
            TenantId = Tenant,
            Title = "Kickoff call notes",
            Description = "Old description",
            Type = ClientActionType.CustomTask,
            Status = ClientActionStatus.Pending,
            StageNumber = 1,
            AssignedToRole = "Client",
            SourceType = ClientActionSourceType.Manual,
            Source = "StaffManual",
            DeadlineUtc = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
            ActivatedAt = DateTime.UtcNow.AddDays(-1)
        };
        configure?.Invoke(task);
        _db.ClientActions.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    private Task<ClientActionResponseDto?> Update(ClientAction task, UpdateClientActionDto dto) =>
        Service.UpdateActionAsync(EngagementId, task.ActionId, Tenant, dto, "staff-1");

    private static object? Prop(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source);

    private (List<string> Fields, List<(string Field, string? From, string? To)> Changes) UpdatedEvent()
    {
        var (_, payload) = Assert.Single(_published, p => p.Type == "ClientActionUpdated");
        var fields = Assert.IsType<List<string>>(Prop(payload, "changedFields"));
        var changes = Assert.IsAssignableFrom<IEnumerable<object>>(Prop(payload, "changes"))
            .Select(c => ((string)Prop(c, "field")!, (string?)Prop(c, "from"), (string?)Prop(c, "to")))
            .ToList();
        return (fields, changes);
    }

    // ==========================================
    // Standard checklist content
    // ==========================================

    [Fact]
    public void DefaultChecklist_HasTheAgreedTasks_InStageOrder()
    {
        var engagementId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var actions = ClientActionService.GenerateDefaultLifecycleActions(engagementId, "tenant-x");

        Assert.Equal(
        [
            (1, "Client Intake & Kickoff Assessment", "CustomTask", "Client", 3),
            (2, "Identity Verification (KYC Passport / ID)", "KycDocument", "Client", 7),
            (2, "Signed Master Services Agreement", "SignAgreement", "Client", 14),
            (2, "Proof of Address", "ProofOfAddress", "Client", 7),
            (3, "Compliance Review & Verification Evaluation", "CustomTask", "Staff", 21),
            (4, "Service Delivery Milestone Sign-off", "CustomTask", "Client", 30),
            (5, "Final Handoff & Ledger Seal", "CustomTask", "Client", 35)
        ],
        actions.Select(a => (a.StageNumber, a.Title, a.Type, a.AssignedToRole, (int)Math.Round((a.DeadlineUtc!.Value - a.CreatedAt).TotalDays))));

        Assert.All(actions, a =>
        {
            Assert.Equal(engagementId, a.EngagementId);
            Assert.Equal("tenant-x", a.TenantId);
            Assert.Equal(ClientActionStatus.Pending, a.Status);
            Assert.Equal("LifecycleDefault", a.Source);
            Assert.Equal(ClientActionSourceType.Lifecycle, a.SourceType);
            Assert.False(a.IsInternalOnly);
            Assert.False(string.IsNullOrWhiteSpace(a.Description));
            Assert.InRange(a.UpdatedAt, before, DateTime.UtcNow);
        });
        Assert.Equal(actions.Count, actions.Select(a => a.ActionId).Distinct().Count());
        // Created timestamps keep the list in order when sorted by creation time.
        Assert.Equal(actions.Select(a => a.Title), actions.OrderBy(a => a.CreatedAt).Select(a => a.Title));
        // Only the first stage is actionable straight away.
        Assert.NotNull(actions[0].ActivatedAt);
        Assert.All(actions.Skip(1), a => Assert.Null(a.ActivatedAt));
    }

    [Fact]
    public void DefaultChecklist_Descriptions_AreTheAgreedText()
    {
        var descriptions = ClientActionService.GenerateDefaultLifecycleActions(Guid.NewGuid(), "t").Select(a => a.Description).ToList();

        Assert.Equal(
        [
            "Review engagement terms, confirm primary point of contact, and outline project objectives.",
            "Upload certified government-issued photo ID or international passport for compliance verification.",
            "Upload signed onboarding contract and service agreements for custodian legal records.",
            "Upload a recent utility bill or bank statement (issued within the last 90 days) confirming your current residential address.",
            "Custodian compliance team evaluates submitted KYC documentation and legal agreements.",
            "Confirm completion of primary engagement deliverables and operational milestone acceptance.",
            "Receive audited compliance report, engagement deliverables receipt, and finalize lifecycle records."
        ], descriptions);
    }

    // ==========================================
    // Update: each field is audited with from/to
    // ==========================================

    [Fact]
    public async Task Update_Description_IsAuditedWithFromAndTo()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { Description = "New description" });

        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["description"], fields);
        Assert.Equal([("description", "Old description", "New description")], changes);
    }

    [Fact]
    public async Task Update_SameDescription_ChangesNothing()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { Description = "Old description" });

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Update_Deadline_IsAudited()
    {
        var task = await SeedTaskAsync();
        var deadline = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        await Update(task, new UpdateClientActionDto { DeadlineUtc = deadline });

        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["deadlineUtc"], fields);
        Assert.Equal([("deadlineUtc", "2026-10-20T00:00:00.0000000Z", "2026-11-01T00:00:00.0000000Z")], changes);
    }

    [Fact]
    public async Task Update_ClearDeadline_IsAudited_AndClearingAnEmptyDeadlineChangesNothing()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { ClearDeadline = true });
        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["deadlineUtc"], fields);
        Assert.Equal([("deadlineUtc", "2026-10-20T00:00:00.0000000Z", null)], changes);

        _published.Clear();
        await Update(task, new UpdateClientActionDto { ClearDeadline = true, DeadlineUtc = DateTime.UtcNow });
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Update_Stage_IsAudited()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { StageNumber = 3 });

        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["stageNumber"], fields);
        Assert.Equal([("stageNumber", "1", "3")], changes);
    }

    [Theory]
    [InlineData("staff", "Staff")]
    [InlineData("STAFF", "Staff")]
    public async Task Update_AssignedRole_IsNormalised_AndAudited(string requested, string stored)
    {
        var task = await SeedTaskAsync();

        var result = await Update(task, new UpdateClientActionDto { AssignedToRole = requested });

        Assert.Equal(stored, result!.AssignedToRole);
        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["assignedToRole"], fields);
        Assert.Equal([("assignedToRole", "Client", stored)], changes);
    }

    [Fact]
    public async Task Update_AssignedRoleBackToClient_IsNormalised()
    {
        var task = await SeedTaskAsync(t => t.AssignedToRole = "Staff");

        var result = await Update(task, new UpdateClientActionDto { AssignedToRole = "client" });

        Assert.Equal("Client", result!.AssignedToRole);
    }

    [Fact]
    public async Task Update_SameAssignedRoleInAnotherCase_ChangesNothing()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { AssignedToRole = "CLIENT" });

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Update_UnknownAssignedRole_IsRejected()
    {
        var task = await SeedTaskAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Update(task, new UpdateClientActionDto { AssignedToRole = "Partner" }));

        Assert.StartsWith("AssignedToRole must be 'Client' or 'Staff'.", error.Message);
    }

    [Fact]
    public async Task Update_InternalOnly_IsAudited()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { IsInternalOnly = true });

        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["isInternalOnly"], fields);
        Assert.Equal([("isInternalOnly", "false", "true")], changes);
    }

    [Fact]
    public async Task Update_SameInternalOnly_ChangesNothing()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { IsInternalOnly = false });

        Assert.Empty(_published);
    }

    [Fact]
    public async Task Update_Type_IsAudited()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { Type = " kycdocument " });

        var (fields, changes) = UpdatedEvent();
        Assert.Equal(["type"], fields);
        Assert.Equal([("type", "CustomTask", "KycDocument")], changes);
    }

    [Fact]
    public async Task Update_SeveralFields_AreAuditedTogether_AndTheEventNamesTheTask()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { Title = "New title", Description = "New description", StageNumber = 2 });

        var (fields, _) = UpdatedEvent();
        Assert.Equal(["title", "description", "stageNumber"], fields);
        var (_, payload) = Assert.Single(_published);
        Assert.Equal(task.ActionId, Prop(payload, "actionId"));
        Assert.Equal("New title", Prop(payload, "title"));
        _audit.Verify(a => a.PublishEventAsync(EngagementId, Tenant, "staff-1", "ClientActionUpdated", It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task Update_PersistsTheChange_AndReturnsTheStaffView()
    {
        var task = await SeedTaskAsync(t => t.IsInternalOnly = false);
        var before = DateTime.UtcNow;

        var result = await Update(task, new UpdateClientActionDto { Description = "Saved" });

        Assert.Equal("Client", result!.AssignedToRole); // hidden in the client view
        Assert.NotNull(result.ActivatedAt);
        _db.ChangeTracker.Clear();
        var stored = await _db.ClientActions.SingleAsync(a => a.ActionId == task.ActionId);
        Assert.Equal("Saved", stored.Description);
        Assert.InRange(stored.UpdatedAt, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task Update_WithNoChanges_StillReturnsTheStaffView()
    {
        var task = await SeedTaskAsync();

        var result = await Update(task, new UpdateClientActionDto());

        Assert.Equal("Client", result!.AssignedToRole);
        Assert.Empty(_published);
    }

    // ==========================================
    // Update: guards
    // ==========================================

    [Theory]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public async Task Update_OnTerminalEngagement_IsRefused(EngagementStatus status)
    {
        var task = await SeedTaskAsync(status: status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(task, new UpdateClientActionDto { Title = "x" }));

        Assert.Equal($"Cannot modify actions for an engagement with status '{status}'.", error.Message);
    }

    [Fact]
    public async Task Update_BlankTitle_IsRejected()
    {
        var task = await SeedTaskAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Update(task, new UpdateClientActionDto { Title = "   " }));

        Assert.StartsWith("Title cannot be empty.", error.Message);
    }

    [Fact]
    public async Task Update_UnknownTaskOrTenant_ReturnsNull()
    {
        var task = await SeedTaskAsync();

        Assert.Null(await Service.UpdateActionAsync(EngagementId, Guid.NewGuid(), Tenant, new UpdateClientActionDto(), "staff-1"));
        Assert.Null(await Service.UpdateActionAsync(EngagementId, task.ActionId, "other-tenant", new UpdateClientActionDto(), "staff-1"));
        Assert.Null(await Service.UpdateActionAsync(EngagementId, task.ActionId, " ", new UpdateClientActionDto(), "staff-1"));
    }

    [Fact]
    public async Task Update_ConditionTask_IsRefused()
    {
        var task = await SeedTaskAsync(t => t.LinkedConditionId = Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(task, new UpdateClientActionDto { Title = "x" }));

        Assert.Equal("This task belongs to a condition; update or deactivate the condition instead.", error.Message);
    }

    [Fact]
    public async Task Update_ConditionSourcedTaskWithoutLink_IsRefused()
    {
        var task = await SeedTaskAsync(t => t.SourceType = ClientActionSourceType.Condition);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Update(task, new UpdateClientActionDto { Title = "x" }));
    }

    // ==========================================
    // Update: Request Information (requirement-linked) tasks
    // ==========================================

    private async Task<(ClientAction Task, Requirement Requirement)> SeedRequirementTaskAsync(string requirementStatus = RequirementStatus.Requested)
    {
        var requirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = EngagementId,
            TenantId = Tenant,
            Status = requirementStatus,
            StageNumber = 1,
            AssignedToRole = "Client"
        };
        _db.Requirements.Add(requirement);
        var task = await SeedTaskAsync(t =>
        {
            t.LinkedRequirementId = requirement.RequirementId;
            t.SourceType = ClientActionSourceType.Requirement;
            t.Type = ClientActionType.Requirement;
        });
        return (task, requirement);
    }

    [Fact]
    public async Task RequirementTask_QuestionAndStage_CanChange_AndTheRequirementMovesWithIt()
    {
        var (task, requirement) = await SeedRequirementTaskAsync();

        var result = await Update(task, new UpdateClientActionDto { Title = "What is your tax number?", StageNumber = 2, AssignedToRole = "client" });

        Assert.Equal("What is your tax number?", result!.Title);
        _db.ChangeTracker.Clear();
        Assert.Equal(2, (await _db.Requirements.SingleAsync(r => r.RequirementId == requirement.RequirementId)).StageNumber);
    }

    [Theory]
    [InlineData("CustomTask", null, null)]
    [InlineData(null, "Staff", null)]
    [InlineData(null, null, true)]
    public async Task RequirementTask_TypeRoleOrVisibility_CannotChange(string? type, string? role, bool? internalOnly)
    {
        var (task, _) = await SeedRequirementTaskAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Update(task, new UpdateClientActionDto { Type = type, AssignedToRole = role, IsInternalOnly = internalOnly }));

        Assert.StartsWith("A Request Information task is always answered by the client", error.Message);
    }

    [Fact]
    public async Task RequirementTask_AlreadyAnswered_IsRefused()
    {
        var (task, _) = await SeedRequirementTaskAsync(RequirementStatus.Submitted);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(task, new UpdateClientActionDto { Title = "x" }));

        Assert.Equal("The client has already answered this question; review the answer instead of editing the question.", error.Message);
    }

    [Fact]
    public async Task RequirementTask_WithMissingRequirement_IsRefused()
    {
        var task = await SeedTaskAsync(t => t.LinkedRequirementId = Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Update(task, new UpdateClientActionDto { Title = "x" }));
    }

    [Fact]
    public async Task RequirementSourcedTaskWithoutLink_IsTreatedAsARequirementTask()
    {
        var task = await SeedTaskAsync(t => t.SourceType = ClientActionSourceType.Requirement);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Update(task, new UpdateClientActionDto { Title = "x" }));

        Assert.StartsWith("The client has already answered this question", error.Message);
    }

    [Fact]
    public async Task ConvertToRequestInformation_CreatesTheRequirement_LinksTheTask_AndPublishesBothEvents()
    {
        var task = await SeedTaskAsync(t => { t.StageNumber = 2; t.AssignedToRole = "Staff"; t.IsInternalOnly = true; });

        var result = await Update(task, new UpdateClientActionDto { Type = " requestinformation " });

        var requirement = await _db.Requirements.SingleAsync();
        Assert.Equal(EngagementId, requirement.EngagementId);
        Assert.Equal(Tenant, requirement.TenantId);
        Assert.Equal(ClientActionService.RequestedInformationRequirementType, requirement.Type);
        Assert.Equal("ClientInformation", ClientActionService.RequestedInformationRequirementType);
        Assert.Equal(RequirementStatus.Requested, requirement.Status);
        Assert.Equal("Client", requirement.AssignedToRole);
        Assert.Equal(2, requirement.StageNumber);
        Assert.Equal("staff-1", requirement.RequestedBy);

        Assert.Equal(ClientActionType.Requirement, result!.Type);
        var stored = await _db.ClientActions.SingleAsync(a => a.ActionId == task.ActionId);
        Assert.Equal(ClientActionSourceType.Requirement, stored.SourceType);
        Assert.Equal("RequirementSync", stored.Source);
        Assert.Equal(requirement.RequirementId, stored.LinkedRequirementId);
        Assert.Equal("Client", stored.AssignedToRole);
        Assert.False(stored.IsInternalOnly);

        var (fields, _) = UpdatedEvent();
        Assert.Equal(["assignedToRole", "type"], fields);

        var (_, requested) = Assert.Single(_published, p => p.Type == "RequirementRequested");
        Assert.Equal(requirement.RequirementId, Prop(requested, "requirementId"));
        Assert.Equal("client-1", Prop(requested, "clientId"));
        Assert.Equal("Kickoff call notes", Prop(requested, "title"));
        Assert.Equal("Client", Prop(requested, "assignedToRole"));
        Assert.Equal("ClientInformation", Prop(requested, "requirementType"));
        Assert.Equal(2, Prop(requested, "stageNumber"));
        Assert.Equal("staff-1", Prop(requested, "requestedBy"));
    }

    [Fact]
    public async Task ConvertToRequestInformation_ClientTask_OnlyRecordsTheTypeChange()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { Type = ClientActionType.RequestInformation });

        var (fields, _) = UpdatedEvent();
        Assert.Equal(["type"], fields);
    }

    [Theory]
    [InlineData("Staff", null)]
    [InlineData(null, true)]
    public async Task ConvertToRequestInformation_ForStaffOrInternal_IsRejected(string? role, bool? internalOnly)
    {
        var task = await SeedTaskAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Update(task, new UpdateClientActionDto { Type = ClientActionType.RequestInformation, AssignedToRole = role, IsInternalOnly = internalOnly }));

        Assert.StartsWith("Request Information is always answered by the client.", error.Message);
        Assert.Empty(_db.Requirements);
    }

    [Fact]
    public async Task ConvertToRequestInformation_AfterADocumentUpload_IsRefused()
    {
        var task = await SeedTaskAsync(t => t.LinkedDocumentId = Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Update(task, new UpdateClientActionDto { Type = ClientActionType.RequestInformation }));

        Assert.Equal("The type can't change after a document was uploaded for this task.", error.Message);
    }

    [Fact]
    public async Task ConvertToRequestInformation_ClientChoosingClientRole_IsAllowed()
    {
        var task = await SeedTaskAsync();

        await Update(task, new UpdateClientActionDto { Type = ClientActionType.RequestInformation, AssignedToRole = "CLIENT" });

        Assert.Single(_db.Requirements);
    }
}
