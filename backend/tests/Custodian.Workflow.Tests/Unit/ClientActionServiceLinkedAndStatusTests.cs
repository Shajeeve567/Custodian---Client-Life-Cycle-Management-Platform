using System.Text.Json;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// ClientActionService: tasks created from another source (requirement, document, condition, meeting),
/// document verification outcomes, and the shared status change (its audit event, actor and stall handling).
/// </summary>
public class ClientActionServiceLinkedAndStatusTests
{
    private const string Tenant = "tenant-001";
    private static readonly Guid EngagementId = Guid.NewGuid();

    private readonly WorkflowDbContext _db = new(new DbContextOptionsBuilder<WorkflowDbContext>()
        .UseInMemoryDatabase("cas-linked-" + Guid.NewGuid().ToString("N")).Options);
    private readonly Mock<IAuditPublisher> _audit = new();
    private readonly List<(string Actor, string Type, object Payload)> _published = [];

    public ClientActionServiceLinkedAndStatusTests()
    {
        _audit.Setup(a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>()))
            .Callback((Guid _, string _, string actor, string type, object payload) => _published.Add((actor, type, payload)))
            .Returns(Task.CompletedTask);
    }

    private ClientActionService Service => new(_db, _audit.Object);

    private async Task SeedEngagementAsync(EngagementStatus status = EngagementStatus.Started, EngagementStage stage = EngagementStage.Onboarding)
    {
        _db.Engagements.Add(new Engagement { EngagementId = EngagementId, TenantId = Tenant, ClientId = "client-1", Status = status, Stage = stage });
        await _db.SaveChangesAsync();
    }

    private async Task<ClientAction> SeedTaskAsync(string status, Action<ClientAction>? configure = null)
    {
        var task = new ClientAction
        {
            EngagementId = EngagementId,
            TenantId = Tenant,
            Title = "Upload passport",
            Type = ClientActionType.KycDocument,
            Status = status,
            AssignedToRole = "Client",
            SourceType = ClientActionSourceType.Manual,
            StageNumber = 1,
            ActivatedAt = DateTime.UtcNow
        };
        configure?.Invoke(task);
        _db.ClientActions.Add(task);
        await _db.SaveChangesAsync();
        return task;
    }

    private static object? Prop(object source, string name) => source.GetType().GetProperty(name)!.GetValue(source);

    private (string Actor, object Payload) StatusChanged()
    {
        var (actor, _, payload) = Assert.Single(_published, p => p.Type == "ClientActionStatusChanged");
        return (actor, payload);
    }

    private static CreateLinkedActionDto Linked(string sourceType, Guid? sourceId = null) => new()
    {
        Title = "Linked task",
        SourceType = sourceType,
        SourceId = sourceId ?? Guid.NewGuid(),
        Type = ""
    };

    // ==========================================
    // CreateLinkedActionAsync
    // ==========================================

    [Theory]
    [InlineData(" ", true)]
    [InlineData("tenant-001", false)]
    public async Task Linked_RequiresTenantAndEngagement(string tenant, bool emptyEngagement)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.CreateLinkedActionAsync(emptyEngagement ? EngagementId : Guid.Empty, tenant, Linked(ClientActionSourceType.Meeting)));

        Assert.StartsWith("TenantId and EngagementId are required.", error.Message);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public async Task Linked_OnTerminalEngagement_IsRefused(EngagementStatus status)
    {
        await SeedEngagementAsync(status);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.CreateLinkedActionAsync(EngagementId, Tenant, Linked(ClientActionSourceType.Meeting)));

        Assert.Equal($"Cannot create actions for an engagement with status '{status}'.", error.Message);
        Assert.Empty(_db.ClientActions);
    }

    [Fact]
    public async Task Linked_EngagementInAnotherTenant_IsNotTreatedAsTerminal()
    {
        _db.Engagements.Add(new Engagement { EngagementId = EngagementId, TenantId = "other", Status = EngagementStatus.Closed });
        await _db.SaveChangesAsync();

        var action = await Service.CreateLinkedActionAsync(EngagementId, Tenant, Linked(ClientActionSourceType.Meeting));

        Assert.Equal(Tenant, action.TenantId);
    }

    [Theory]
    [InlineData("", "SourceType is required.")]
    [InlineData("  ", "SourceType is required.")]
    [InlineData("Invoice", "Invalid source type 'Invoice'.")]
    public async Task Linked_SourceTypeMustBeKnown(string sourceType, string message)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateLinkedActionAsync(EngagementId, Tenant, Linked(sourceType)));

        Assert.StartsWith(message, error.Message);
    }

    [Fact]
    public async Task Linked_EmptySourceId_IsRejected()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            Service.CreateLinkedActionAsync(EngagementId, Tenant, Linked(ClientActionSourceType.Meeting, Guid.Empty)));

        Assert.StartsWith("SourceId must be a non-empty Guid.", error.Message);
    }

    [Theory]
    [InlineData(ClientActionSourceType.Meeting, ClientActionType.Meeting)]
    [InlineData(ClientActionSourceType.Condition, ClientActionType.Approval)]
    [InlineData(ClientActionSourceType.Requirement, ClientActionType.CustomTask)]
    [InlineData(ClientActionSourceType.Document, ClientActionType.CustomTask)]
    public async Task Linked_TypeDefaultsFromTheSource(string sourceType, string expectedType)
    {
        var dto = Linked(sourceType);
        dto.Type = "  ";

        var action = await Service.CreateLinkedActionAsync(EngagementId, Tenant, dto);

        Assert.Equal(expectedType, action.Type);
    }

    [Fact]
    public async Task Linked_ExplicitType_IsKept()
    {
        var dto = Linked(ClientActionSourceType.Meeting);
        dto.Type = ClientActionType.Payment;

        Assert.Equal(ClientActionType.Payment, (await Service.CreateLinkedActionAsync(EngagementId, Tenant, dto)).Type);
    }

    [Theory]
    [InlineData(ClientActionSourceType.Requirement)]
    [InlineData(ClientActionSourceType.Document)]
    [InlineData(ClientActionSourceType.Condition)]
    [InlineData(ClientActionSourceType.Meeting)]
    public async Task Linked_RecordsTheSource_AndLinksOnlyTheMatchingId(string sourceType)
    {
        var sourceId = Guid.NewGuid();

        var action = await Service.CreateLinkedActionAsync(EngagementId, Tenant, Linked($"  {sourceType} ", sourceId));

        Assert.Equal(sourceType, action.SourceType);
        Assert.Equal($"{sourceType}:{sourceId}", action.Source);
        Assert.Equal(sourceType == ClientActionSourceType.Requirement ? sourceId : null, action.LinkedRequirementId);
        Assert.Equal(sourceType == ClientActionSourceType.Document ? sourceId : null, action.LinkedDocumentId);
        Assert.Equal(sourceType == ClientActionSourceType.Condition ? sourceId : null, action.LinkedConditionId);
        Assert.Equal(sourceType == ClientActionSourceType.Meeting ? sourceId : null, action.LinkedMeetingId);
    }

    [Fact]
    public async Task Linked_IsSaved_WithTheDtoValues()
    {
        await SeedEngagementAsync();
        var deadline = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var dto = Linked(ClientActionSourceType.Meeting);
        dto.Description = "Bring ID";
        dto.DeadlineUtc = deadline;
        dto.IsInternalOnly = true;
        dto.AssignedToRole = "Staff";
        dto.SourceMetadata = "{\"x\":1}";
        dto.StageNumber = 1;

        var action = await Service.CreateLinkedActionAsync(EngagementId, Tenant, dto);

        _db.ChangeTracker.Clear();
        var stored = await _db.ClientActions.SingleAsync();
        Assert.Equal(action.ActionId, stored.ActionId);
        Assert.Equal("Linked task", stored.Title);
        Assert.Equal("Bring ID", stored.Description);
        Assert.Equal(ClientActionStatus.Pending, stored.Status);
        Assert.Equal(deadline, stored.DeadlineUtc);
        Assert.True(stored.IsInternalOnly);
        Assert.Equal("Staff", stored.AssignedToRole);
        Assert.Equal("{\"x\":1}", stored.SourceMetadata);
        Assert.NotNull(stored.ActivatedAt); // stage 1 of a started engagement is open
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-2, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public async Task Linked_StageBelowOne_DefaultsToStageOne(int requested, int expected)
    {
        var dto = Linked(ClientActionSourceType.Meeting);
        dto.StageNumber = requested;

        Assert.Equal(expected, (await Service.CreateLinkedActionAsync(EngagementId, Tenant, dto)).StageNumber);
    }

    [Fact]
    public async Task LinkedOverload_DefaultsTypeAndRole()
    {
        var action = await Service.CreateLinkedActionAsync(EngagementId, Tenant, ClientActionSourceType.Requirement, Guid.NewGuid(),
            "Question", type: null, assignedToRole: null);

        Assert.Equal(ClientActionType.CustomTask, action.Type);
        Assert.Equal("Client", action.AssignedToRole);
    }

    [Fact]
    public async Task LinkedOverload_PassesEveryValueThrough()
    {
        var sourceId = Guid.NewGuid();
        var deadline = DateTime.UtcNow.AddDays(5);

        var action = await Service.CreateLinkedActionAsync(EngagementId, Tenant, ClientActionSourceType.Meeting, sourceId, "Meet",
            "Agenda", ClientActionType.Meeting, 2, deadline, "Staff", true, "{}");

        Assert.Equal(("Meet", "Agenda", ClientActionType.Meeting, 2, deadline, "Staff", true, "{}", sourceId),
            (action.Title, action.Description, action.Type, action.StageNumber, action.DeadlineUtc, action.AssignedToRole, action.IsInternalOnly, action.SourceMetadata, action.LinkedMeetingId));
    }

    // ==========================================
    // CancelActionsForSourceAsync
    // ==========================================

    [Fact]
    public async Task CancelForSource_CancelsOnlyOpenTasksOfThatSource()
    {
        await SeedEngagementAsync();
        var conditionId = Guid.NewGuid();
        var open = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.SourceType = ClientActionSourceType.Condition; t.LinkedConditionId = conditionId; });
        var done = await SeedTaskAsync(ClientActionStatus.Completed, t => { t.SourceType = ClientActionSourceType.Condition; t.LinkedConditionId = conditionId; });
        var otherSource = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.SourceType = ClientActionSourceType.Condition; t.LinkedConditionId = Guid.NewGuid(); });
        var otherType = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.SourceType = ClientActionSourceType.Meeting; t.LinkedMeetingId = conditionId; });

        await Service.CancelActionsForSourceAsync(EngagementId, Tenant, ClientActionSourceType.Condition, conditionId, "staff-1", "Deactivated");

        _db.ChangeTracker.Clear();
        var statuses = await _db.ClientActions.ToDictionaryAsync(a => a.ActionId, a => a.Status);
        Assert.Equal(ClientActionStatus.Cancelled, statuses[open.ActionId]);
        Assert.Equal(ClientActionStatus.Completed, statuses[done.ActionId]);
        Assert.Equal(ClientActionStatus.Pending, statuses[otherSource.ActionId]);
        Assert.Equal(ClientActionStatus.Pending, statuses[otherType.ActionId]);
        var (actor, payload) = StatusChanged();
        Assert.Equal("staff-1", actor);
        Assert.Equal("Deactivated", Prop(payload, "reason"));
    }

    [Theory]
    [InlineData(ClientActionSourceType.Requirement)]
    [InlineData(ClientActionSourceType.Document)]
    [InlineData(ClientActionSourceType.Meeting)]
    public async Task CancelForSource_MatchesEachKindOfLink(string sourceType)
    {
        await SeedEngagementAsync();
        var sourceId = Guid.NewGuid();
        await SeedTaskAsync(ClientActionStatus.Uploaded, t =>
        {
            t.SourceType = sourceType;
            if (sourceType == ClientActionSourceType.Requirement) t.LinkedRequirementId = sourceId;
            if (sourceType == ClientActionSourceType.Document) t.LinkedDocumentId = sourceId;
            if (sourceType == ClientActionSourceType.Meeting) t.LinkedMeetingId = sourceId;
        });

        await Service.CancelActionsForSourceAsync(EngagementId, Tenant, sourceType, sourceId, "staff-1", "r");

        Assert.Equal(ClientActionStatus.Cancelled, (await _db.ClientActions.SingleAsync()).Status);
    }

    [Fact]
    public async Task CancelForSource_OtherTenantOrEngagement_IsUntouched()
    {
        await SeedEngagementAsync();
        var sourceId = Guid.NewGuid();
        await SeedTaskAsync(ClientActionStatus.Pending, t => { t.SourceType = ClientActionSourceType.Meeting; t.LinkedMeetingId = sourceId; });

        await Service.CancelActionsForSourceAsync(EngagementId, "other", ClientActionSourceType.Meeting, sourceId, "a", "r");
        await Service.CancelActionsForSourceAsync(Guid.NewGuid(), Tenant, ClientActionSourceType.Meeting, sourceId, "a", "r");

        Assert.Equal(ClientActionStatus.Pending, (await _db.ClientActions.SingleAsync()).Status);
        Assert.Empty(_published);
    }

    [Theory]
    [InlineData(" ", false, "Meeting", false)]
    [InlineData("tenant-001", true, "Meeting", false)]
    [InlineData("tenant-001", false, " ", false)]
    [InlineData("tenant-001", false, "Meeting", true)]
    public async Task CancelForSource_MissingArguments_DoNothing(string tenant, bool emptyEngagement, string sourceType, bool emptySource)
    {
        await SeedEngagementAsync();
        var sourceId = Guid.NewGuid();
        await SeedTaskAsync(ClientActionStatus.Pending, t => { t.SourceType = ClientActionSourceType.Meeting; t.LinkedMeetingId = sourceId; });

        await Service.CancelActionsForSourceAsync(emptyEngagement ? Guid.Empty : EngagementId, tenant, sourceType, emptySource ? Guid.Empty : sourceId, "a", "r");

        Assert.Equal(ClientActionStatus.Pending, (await _db.ClientActions.SingleAsync()).Status);
    }

    // ==========================================
    // ApplyVerificationOutcomeAsync
    // ==========================================

    private static string Metadata(string? documentId = "doc-1", string? compliance = "NeedsReview") =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["documentId"] = documentId, ["complianceStatus"] = compliance });

    private static Dictionary<string, JsonElement> ReadMetadata(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json!)!;

    [Fact]
    public async Task Verified_CompletesTheTask_AndRecordsTheDecisionInMetadata()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded, t => t.SourceMetadata = Metadata());

        var result = await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant,
            new ApplyActionVerificationDto { VerificationStatus = "verified", VerifiedBy = "staff-1", VerificationReason = "Looks good" });

        Assert.Equal(ClientActionStatus.Completed, result!.Status);
        var meta = ReadMetadata(result.SourceMetadata);
        Assert.Equal("doc-1", meta["documentId"].GetString());
        Assert.Equal("NeedsReview", meta["complianceStatus"].GetString());
        Assert.Equal("Verified", meta["verificationStatus"].GetString());
        Assert.Equal("staff-1", meta["verifiedBy"].GetString());
        Assert.Equal("Looks good", meta["verificationReason"].GetString());
        Assert.False(meta.ContainsKey("rejectionReason"));

        var (actor, payload) = StatusChanged();
        Assert.Equal("staff-1", actor);
        Assert.Equal("Looks good", Prop(payload, "reason"));
    }

    [Fact]
    public async Task Verified_WithoutReasonOrCompliance_UsesDefaults()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded);

        var result = await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant,
            new ApplyActionVerificationDto { VerificationStatus = "Verified", VerifiedBy = "staff-1" });

        var meta = ReadMetadata(result!.SourceMetadata);
        Assert.Equal("Compliant", meta["complianceStatus"].GetString());
        Assert.Equal(JsonValueKind.Null, meta["documentId"].ValueKind);
        Assert.Equal(JsonValueKind.Null, meta["verificationReason"].ValueKind);
        Assert.Equal("DocumentVerified", Prop(StatusChanged().Payload, "reason"));
    }

    [Fact]
    public async Task Rejected_RejectsTheTask_WithATrimmedReason()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded, t => t.SourceMetadata = Metadata());

        var result = await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant,
            new ApplyActionVerificationDto { VerificationStatus = "REJECTED", VerifiedBy = "staff-1", VerificationReason = "  Blurry scan " });

        Assert.Equal(ClientActionStatus.Rejected, result!.Status);
        var meta = ReadMetadata(result.SourceMetadata);
        Assert.Equal("doc-1", meta["documentId"].GetString());
        Assert.Equal("NeedsReview", meta["complianceStatus"].GetString());
        Assert.Equal("Rejected", meta["verificationStatus"].GetString());
        Assert.Equal("staff-1", meta["verifiedBy"].GetString());
        Assert.Equal("Blurry scan", meta["verificationReason"].GetString());
        Assert.Equal("Blurry scan", meta["rejectionReason"].GetString());
        Assert.Equal("Blurry scan", Prop(StatusChanged().Payload, "reason"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Rejected_WithoutReason_UsesTheDefaultMessage_AndDefaultCompliance(string? reason)
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded);

        var result = await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant,
            new ApplyActionVerificationDto { VerificationStatus = "Rejected", VerificationReason = reason });

        var meta = ReadMetadata(result!.SourceMetadata);
        Assert.Equal("Document verification was rejected by staff.", meta["rejectionReason"].GetString());
        Assert.Equal("Compliant", meta["complianceStatus"].GetString());
    }

    [Fact]
    public async Task Verification_UnknownStatus_IsRejected()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant,
            new ApplyActionVerificationDto { VerificationStatus = "Maybe" }));

        Assert.StartsWith("Invalid verification status 'Maybe'. Must be 'Verified' or 'Rejected'.", error.Message);
    }

    [Fact]
    public async Task Verification_FindsTheTask_ByEngagementOrAnyEngagement_InTheTenantOnly()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded);
        var dto = () => new ApplyActionVerificationDto { VerificationStatus = "Rejected" };

        Assert.Null(await Service.ApplyVerificationOutcomeAsync(Guid.NewGuid(), task.ActionId, Tenant, dto()));
        Assert.Null(await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, "other", dto()));
        Assert.Null(await Service.ApplyVerificationOutcomeAsync(EngagementId, Guid.NewGuid(), Tenant, dto()));
        Assert.Null(await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, " ", dto()));
        Assert.NotNull(await Service.ApplyVerificationOutcomeAsync(Guid.Empty, task.ActionId, Tenant, dto()));
    }

    // ==========================================
    // The shared status change
    // ==========================================

    [Fact]
    public async Task StatusChange_PublishesFromToSourceAndReason()
    {
        await SeedEngagementAsync();
        var documentId = Guid.NewGuid();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded, t => { t.SourceType = ClientActionSourceType.Document; t.LinkedDocumentId = documentId; });

        await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant, new ApplyActionVerificationDto { VerificationStatus = "Verified", VerifiedBy = "staff-1" });

        var (_, payload) = StatusChanged();
        Assert.Equal(task.ActionId, Prop(payload, "actionId"));
        Assert.Equal("Upload passport", Prop(payload, "title"));
        Assert.Equal(ClientActionStatus.Uploaded, Prop(payload, "fromStatus"));
        Assert.Equal(ClientActionStatus.Completed, Prop(payload, "toStatus"));
        Assert.Equal(ClientActionSourceType.Document, Prop(payload, "sourceType"));
        Assert.Equal(documentId, Prop(payload, "sourceId"));
        _audit.Verify(a => a.PublishEventAsync(EngagementId, Tenant, "staff-1", "ClientActionStatusChanged", It.IsAny<object>()), Times.Once);
    }

    public static TheoryData<string, string> SourceIdPrecedence => new()
    {
        { "requirement,document,condition,meeting", "requirement" },
        { "document,condition,meeting", "document" },
        { "condition,meeting", "condition" },
        { "meeting", "meeting" }
    };

    [Theory]
    [MemberData(nameof(SourceIdPrecedence))]
    public async Task StatusChange_SourceId_PrefersRequirementThenDocumentThenConditionThenMeeting(string links, string expected)
    {
        await SeedEngagementAsync();
        var ids = new Dictionary<string, Guid>
        {
            ["requirement"] = Guid.NewGuid(), ["document"] = Guid.NewGuid(), ["condition"] = Guid.NewGuid(), ["meeting"] = Guid.NewGuid()
        };
        var present = links.Split(',');
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded, t =>
        {
            if (present.Contains("requirement")) t.LinkedRequirementId = ids["requirement"];
            if (present.Contains("document")) t.LinkedDocumentId = ids["document"];
            if (present.Contains("condition")) t.LinkedConditionId = ids["condition"];
            if (present.Contains("meeting")) t.LinkedMeetingId = ids["meeting"];
        });

        await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant, new ApplyActionVerificationDto { VerificationStatus = "Rejected" });

        Assert.Equal(ids[expected], Prop(StatusChanged().Payload, "sourceId"));
    }

    [Fact]
    public async Task StatusChange_WithoutActor_IsAuditedAsSystem_AndKeepsThePreviousCompletedBy()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded, t => t.CompletedByActor = "uploader");

        // VerifiedBy defaults to "" (the controller always fills it from the JWT); null exercises the "System" fallback.
        var result = await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant,
            new ApplyActionVerificationDto { VerificationStatus = "Rejected", VerifiedBy = null! });

        Assert.Equal("System", StatusChanged().Actor);
        Assert.Equal("uploader", result!.CompletedByActor);
        Assert.Null(result.CompletedAt);
    }

    [Fact]
    public async Task StatusChange_ToCompleted_StampsCompletedAtAndActor()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Uploaded);
        var before = DateTime.UtcNow;

        var result = await Service.ApplyVerificationOutcomeAsync(EngagementId, task.ActionId, Tenant, new ApplyActionVerificationDto { VerificationStatus = "Verified", VerifiedBy = "staff-1" });

        Assert.Equal("staff-1", result!.CompletedByActor);
        Assert.InRange(result.CompletedAt!.Value, before, DateTime.UtcNow);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public async Task StatusChange_OnTerminalEngagement_IsRefused(EngagementStatus status)
    {
        await SeedEngagementAsync(status);
        var task = await SeedTaskAsync(ClientActionStatus.Pending);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CancelActionAsync(EngagementId, task.ActionId, Tenant, "r", "staff-1"));

        Assert.Equal($"Cannot modify actions for an engagement with status '{status}'.", error.Message);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task StatusChange_ToTheSameStatus_PublishesNothing()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Cancelled);

        var result = await Service.CancelActionAsync(EngagementId, task.ActionId, Tenant, "again", "staff-1");

        Assert.Equal(ClientActionStatus.Cancelled, result!.Status);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task Cancel_ResolvesTheTasksOpenStall_Only()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Pending);
        var openStall = new StallRecord { TenantId = Tenant, EngagementId = EngagementId, ActionId = task.ActionId };
        var alreadyResolved = new StallRecord { TenantId = Tenant, EngagementId = EngagementId, ActionId = task.ActionId, ResolvedAtUtc = DateTime.UtcNow.AddDays(-1), Resolution = "Earlier" };
        var otherTenant = new StallRecord { TenantId = "other", EngagementId = EngagementId, ActionId = task.ActionId };
        var otherAction = new StallRecord { TenantId = Tenant, EngagementId = EngagementId, ActionId = Guid.NewGuid() };
        _db.StallRecords.AddRange(openStall, alreadyResolved, otherTenant, otherAction);
        await _db.SaveChangesAsync();

        await Service.CancelActionAsync(EngagementId, task.ActionId, Tenant, "  Not needed ", "staff-1");

        _db.ChangeTracker.Clear();
        var stalls = await _db.StallRecords.ToDictionaryAsync(s => s.StallId);
        Assert.Equal(StallResolution.ActionCancelled, stalls[openStall.StallId].Resolution);
        Assert.NotNull(stalls[openStall.StallId].ResolvedAtUtc);
        Assert.Equal("Earlier", stalls[alreadyResolved.StallId].Resolution);
        Assert.Null(stalls[otherTenant.StallId].ResolvedAtUtc);
        Assert.Null(stalls[otherAction.StallId].ResolvedAtUtc);
        Assert.Single(_published, p => p.Type == Custodian.Workflow.Services.Stall.StallRecorder.ResolvedEventType);
        Assert.Equal("Not needed", Prop(StatusChanged().Payload, "reason"));
    }

    [Fact]
    public async Task Cancel_RequiresAReason()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Pending);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => Service.CancelActionAsync(EngagementId, task.ActionId, Tenant, " ", "staff-1"));

        Assert.StartsWith("A reason is required to cancel a task.", error.Message);
    }

    [Fact]
    public async Task Cancel_UnknownTaskOrTenant_ReturnsNull()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Pending);

        Assert.Null(await Service.CancelActionAsync(EngagementId, Guid.NewGuid(), Tenant, "r", "a"));
        Assert.Null(await Service.CancelActionAsync(EngagementId, task.ActionId, "other", "r", "a"));
        Assert.Null(await Service.CancelActionAsync(EngagementId, task.ActionId, " ", "r", "a"));
    }

    [Fact]
    public async Task Cancel_ReturnsTheStaffView()
    {
        await SeedEngagementAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Pending);

        var result = await Service.CancelActionAsync(EngagementId, task.ActionId, Tenant, "r", "staff-1");

        Assert.Equal("Client", result!.AssignedToRole);
        Assert.NotNull(result.ActivatedAt);
    }

    // ==========================================
    // Stage activation and client block reasons
    // ==========================================

    [Fact]
    public async Task ActivateStage_ActivatesOnlyThatStagesWaitingTasks()
    {
        var waiting = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.StageNumber = 2; t.ActivatedAt = null; });
        var earlier = DateTime.UtcNow.AddDays(-3);
        var alreadyOpen = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.StageNumber = 2; t.ActivatedAt = earlier; });
        var laterStage = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.StageNumber = 3; t.ActivatedAt = null; });
        var otherTenant = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.StageNumber = 2; t.ActivatedAt = null; t.TenantId = "other"; });
        var before = DateTime.UtcNow;

        await Service.ActivateStageActionsAsync(EngagementId, Tenant, 2);

        _db.ChangeTracker.Clear();
        var all = await _db.ClientActions.ToDictionaryAsync(a => a.ActionId);
        Assert.InRange(all[waiting.ActionId].ActivatedAt!.Value, before, DateTime.UtcNow);
        Assert.InRange(all[waiting.ActionId].UpdatedAt, before, DateTime.UtcNow);
        Assert.Equal(earlier, all[alreadyOpen.ActionId].ActivatedAt);
        Assert.Null(all[laterStage.ActionId].ActivatedAt);
        Assert.Null(all[otherTenant.ActionId].ActivatedAt);
    }

    [Theory]
    [InlineData(" ", false, 2)]
    [InlineData("tenant-001", true, 2)]
    [InlineData("tenant-001", false, 0)]
    public async Task ActivateStage_InvalidArguments_DoNothing(string tenant, bool emptyEngagement, int stage)
    {
        await SeedTaskAsync(ClientActionStatus.Pending, t => { t.StageNumber = 0; t.ActivatedAt = null; });
        await SeedTaskAsync(ClientActionStatus.Pending, t => { t.StageNumber = 2; t.ActivatedAt = null; });

        await Service.ActivateStageActionsAsync(emptyEngagement ? Guid.Empty : EngagementId, tenant, stage);

        Assert.All(_db.ClientActions, a => Assert.Null(a.ActivatedAt));
    }

    [Theory]
    [InlineData(true, "Client", null, null, "This task is handled by the Custodian team.")]
    [InlineData(false, "Staff", null, null, "This task is handled by the Custodian team.")]
    [InlineData(false, "client", ClientActionType.KycDocument, null, "This task needs a document upload, which the Custodian team verifies. Please upload the document instead.")]
    [InlineData(false, "Client", ClientActionType.CustomTask, ClientActionSourceType.Condition, "This approval or payment is confirmed by the Custodian team.")]
    [InlineData(false, "Client", ClientActionType.CustomTask, null, null)]
    public async Task ClientCompletionBlockReason_ExplainsWhyAClientCannotTickATaskOff(
        bool internalOnly, string role, string? type, string? sourceType, string? expected)
    {
        var task = await SeedTaskAsync(ClientActionStatus.Pending, t =>
        {
            t.IsInternalOnly = internalOnly;
            t.AssignedToRole = role;
            t.Type = type ?? ClientActionType.CustomTask;
            t.SourceType = sourceType ?? ClientActionSourceType.Manual;
        });

        Assert.Equal(expected, await Service.GetClientCompletionBlockReasonAsync(EngagementId, task.ActionId, Tenant));
    }

    [Fact]
    public async Task ClientCompletionBlockReason_ConditionLinkedTask_IsBlocked()
    {
        var task = await SeedTaskAsync(ClientActionStatus.Pending, t => { t.Type = ClientActionType.CustomTask; t.LinkedConditionId = Guid.NewGuid(); });

        Assert.Equal("This approval or payment is confirmed by the Custodian team.",
            await Service.GetClientCompletionBlockReasonAsync(EngagementId, task.ActionId, Tenant));
    }

    [Fact]
    public async Task ClientCompletionBlockReason_UnknownTaskOrTenant_IsNull()
    {
        var task = await SeedTaskAsync(ClientActionStatus.Pending, t => t.IsInternalOnly = true);

        Assert.Null(await Service.GetClientCompletionBlockReasonAsync(EngagementId, Guid.NewGuid(), Tenant));
        Assert.Null(await Service.GetClientCompletionBlockReasonAsync(Guid.NewGuid(), task.ActionId, Tenant));
        Assert.Null(await Service.GetClientCompletionBlockReasonAsync(EngagementId, task.ActionId, "other"));
        Assert.Null(await Service.GetClientCompletionBlockReasonAsync(EngagementId, task.ActionId, " "));
    }

    [Fact]
    public async Task ClientAvailabilityBlockReason_DependsOnEngagementStatusAndActivation()
    {
        await SeedEngagementAsync(EngagementStatus.Draft);
        var task = await SeedTaskAsync(ClientActionStatus.Pending);

        Assert.Equal("Your onboarding hasn't started yet. This task opens once your Custodian team starts it.",
            await Service.GetClientAvailabilityBlockReasonAsync(EngagementId, task.ActionId, Tenant));
        Assert.Null(await Service.GetClientAvailabilityBlockReasonAsync(EngagementId, Guid.NewGuid(), Tenant));
        Assert.Null(await Service.GetClientAvailabilityBlockReasonAsync(EngagementId, task.ActionId, "other"));
        Assert.Null(await Service.GetClientAvailabilityBlockReasonAsync(EngagementId, task.ActionId, " "));
    }

    [Fact]
    public async Task ClientAvailabilityBlockReason_EngagementInAnotherTenant_CountsAsNotStarted()
    {
        _db.Engagements.Add(new Engagement { EngagementId = EngagementId, TenantId = "other", Status = EngagementStatus.Started });
        await _db.SaveChangesAsync();
        var task = await SeedTaskAsync(ClientActionStatus.Pending);

        Assert.StartsWith("Your onboarding hasn't started yet.", await Service.GetClientAvailabilityBlockReasonAsync(EngagementId, task.ActionId, Tenant));
    }

    [Theory]
    [InlineData(EngagementStatus.Started, true, 2, null)]
    [InlineData(EngagementStatus.Started, false, 2, "This task opens in Stage 2.")]
    [InlineData(EngagementStatus.Closed, true, 1, "Your onboarding hasn't started yet. This task opens once your Custodian team starts it.")]
    [InlineData(null, true, 1, "Your onboarding hasn't started yet. This task opens once your Custodian team starts it.")]
    public void ClientAvailabilityBlockReason_Rule(EngagementStatus? status, bool activated, int stage, string? expected)
    {
        Assert.Equal(expected, ClientActionService.ClientAvailabilityBlockReason(status, activated ? DateTime.UtcNow : null, stage));
    }
}
