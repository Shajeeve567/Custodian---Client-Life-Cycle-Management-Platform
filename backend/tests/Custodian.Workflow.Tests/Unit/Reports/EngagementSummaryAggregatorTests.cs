using System.Reflection;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Reports;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>
/// CSTD-78: the engagement summary aggregation rules (CSTD-76). Now = 2026-10-12 12:00 UTC.
///
/// Scenarios (built by <see cref="Scenario"/>):
///   empty             Started, stage 1, no actions/conditions/documents.
///   in-progress       Started, stage 2: 1 pending KYC (due Oct 15), 1 overdue upload (due Oct 10),
///                     2 completed tasks, 1 internal-only and 1 cancelled evidence task;
///                     approval satisfied Oct 05 + payment pending due Oct 20; 2 documents (1 verified, 1 under review).
///   closed            Closed Oct 11, all tasks complete; approval and payment satisfied.
///   rejected-docs     Started: one document rejected by staff, one by the automatic check, one checking.
///   overdue-payment   Started: payment pending, due Oct 01 (overdue); approval pending, no due date.
///   no-conditions     Started with tasks and a document but no conditions.
/// </summary>
public class EngagementSummaryAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static DateTime Oct(int day) => new(2026, 10, day, 0, 0, 0, DateTimeKind.Unspecified); // as MySQL returns it

    [Theory]
    //          scenario           outstanding completed approvals payments requested submitted compliant verified rejected final overduePayments
    [InlineData("empty",            0,          0,        0,        0,       0,        0,        0,        0,       0,       false, 0)]
    [InlineData("in-progress",      2,          2,        1,        1,       2,        2,        2,        1,       0,       false, 0)]
    [InlineData("closed",           0,          2,        1,        1,       1,        1,        1,        1,       0,       true,  0)]
    [InlineData("rejected-docs",    1,          0,        0,        0,       3,        3,        1,        0,       2,       false, 0)]
    [InlineData("overdue-payment",  0,          0,        1,        1,       0,        0,        0,        0,       0,       false, 1)]
    [InlineData("no-conditions",    1,          1,        0,        0,       1,        1,        1,        0,       0,       false, 0)]
    public void Aggregate_Scenario_ProducesExpectedSections(
        string scenario, int outstanding, int completed, int approvals, int payments,
        int requested, int submitted, int compliant, int verified, int rejected, bool isFinal, int overduePayments)
    {
        var data = EngagementSummaryAggregator.Aggregate(Scenario(scenario));

        Assert.Equal(outstanding, data.OutstandingActions.Count);
        Assert.Equal(completed, data.RecentlyCompletedActions.Count);
        Assert.Equal(approvals, data.Approvals.Count);
        Assert.Equal(payments, data.Payments.Count);
        Assert.Equal(new SummaryDocumentCounts(requested, submitted, compliant, verified, rejected), data.DocumentCounts);
        Assert.Equal(isFinal, data.Overview.IsFinal);
        Assert.Equal(overduePayments, data.Payments.Count(p => p.IsOverdue));
        Assert.Null(data.Staff);
        Assert.Equal("ENG-" + data.EngagementId.ToString("N")[..6].ToUpperInvariant(), data.EngagementCode);
        AssertAllDatesUtc(data);
    }

    [Fact]
    public void InProgress_OutstandingActions_AreMostUrgentFirst_AndNextStepIsThePrimaryAction()
    {
        var data = EngagementSummaryAggregator.Aggregate(Scenario("in-progress"));

        Assert.Equal(["Upload bank statement", "Provide passport"], data.OutstandingActions.Select(a => a.Title));
        Assert.True(data.OutstandingActions[0].IsOverdue);
        Assert.NotNull(data.NextStep);
        Assert.Equal("Provide passport", data.NextStep!.Title);
        Assert.Equal("Clear photo of the photo page", data.NextStep.Detail);
    }

    [Fact]
    public void InProgress_CompletedConditionsCarryTheirCompletionDate_OpenOnesDoNot()
    {
        var data = EngagementSummaryAggregator.Aggregate(Scenario("in-progress"));

        var approval = Assert.Single(data.Approvals);
        Assert.Equal("Approved", approval.Status);
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), approval.CompletedAtUtc);
        Assert.Equal(DateTimeKind.Utc, approval.CompletedAtUtc!.Value.Kind);

        var payment = Assert.Single(data.Payments);
        Assert.Equal(ConditionStatus.Pending, payment.Status);
        Assert.Null(payment.CompletedAtUtc);
        Assert.Equal(2500m, payment.Amount);
        Assert.Equal("LKR", payment.Currency);
        Assert.False(payment.IsOverdue);
    }

    [Fact]
    public void Closed_HasFinalStateAndCloseDate_AndNoOutstandingWorkOrNextStep()
    {
        var data = EngagementSummaryAggregator.Aggregate(Scenario("closed"));

        Assert.True(data.Overview.IsFinal);
        Assert.Equal("Closed", data.Overview.Status);
        Assert.Equal(new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc), data.Overview.ClosedAtUtc);
        Assert.Null(data.NextStep);
        Assert.Empty(data.OutstandingActions);
    }

    [Fact]
    public void RejectedDocuments_ShowClientStatusAndTheLaterReason()
    {
        var rows = EngagementSummaryAggregator.Aggregate(Scenario("rejected-docs")).Documents;

        var byStaff = rows.Single(r => r.Type == "Passport");
        Assert.Equal(EngagementSummaryAggregator.DocumentRejected, byStaff.Status);
        Assert.Equal("Photo is blurred", byStaff.RejectionReason);

        var byCheck = rows.Single(r => r.Type == "ProofOfAddress");
        Assert.Equal(EngagementSummaryAggregator.DocumentRejected, byCheck.Status);
        Assert.Equal("Document is expired", byCheck.RejectionReason);

        var checking = rows.Single(r => r.Type == "BankStatement");
        Assert.Equal(EngagementSummaryAggregator.DocumentChecking, checking.Status);
        Assert.Null(checking.RejectionReason);
    }

    [Theory]
    [InlineData("Pending", "Unverified", "Checking")]
    [InlineData("Compliant", "Unverified", "Under review")]
    [InlineData("Compliant", "Pending", "Under review")]
    [InlineData("Compliant", "Verified", "Verified")]
    [InlineData("Compliant", "Rejected", "Rejected")]
    [InlineData("Rejected", "Unverified", "Rejected")]
    public void DocumentStatus_UsesClientWording(string compliance, string verification, string expected)
    {
        var doc = new DocumentSummaryDto { Type = "Passport", ComplianceStatus = compliance, VerificationStatus = verification };
        Assert.Equal(expected, EngagementSummaryAggregator.DocumentStatus(doc));
    }

    [Fact]
    public void DeletedDocuments_AreNotListedOrCounted()
    {
        var inputs = Scenario("empty") with
        {
            Documents = [Doc("Passport", "Compliant", "Verified", deleted: true)]
        };

        var data = EngagementSummaryAggregator.Aggregate(inputs);

        Assert.Empty(data.Documents);
        Assert.Equal(SummaryDocumentCounts.Zero, data.DocumentCounts);
    }

    [Fact]
    public void RecentlyCompleted_IsNewestFirst_AndCapped()
    {
        var actions = Enumerable.Range(1, 12)
            .Select(i => Action($"Task {i:00}", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(i)))
            .ToList();
        var inputs = Scenario("empty") with { ClientVisibleActions = actions };

        var rows = EngagementSummaryAggregator.Aggregate(inputs).RecentlyCompletedActions;

        Assert.Equal(EngagementSummaryAggregator.RecentlyCompletedLimit, rows.Count);
        Assert.Equal("Task 12", rows[0].Title);
        Assert.Equal("Task 03", rows[^1].Title);
    }

    [Fact]
    public void StaffAudience_AddsStaffDetails_ClientAudienceNever()
    {
        var client = EngagementSummaryAggregator.Aggregate(Scenario("in-progress"));
        var staff = EngagementSummaryAggregator.Aggregate(Scenario("in-progress") with { Audience = EngagementSummaryAudience.Staff });

        Assert.Null(client.Staff);
        Assert.Equal(new StaffSummaryDetails("client-1", "staff-1"), staff.Staff);
    }

    /// <summary>
    /// The client-safe mapping is structural: no type reachable from the report data has a field for
    /// internal or identifying values, apart from the staff-only <see cref="StaffSummaryDetails"/>.
    /// </summary>
    [Fact]
    public void ReportData_HasNoStaffOnlyOrInternalFields()
    {
        string[] forbidden =
        [
            "TenantId", "ClientId", "StaffId", "TargetClientId", "InternalNote", "IsInternalOnly", "SourceMetadata",
            "CreatedBy", "UpdatedBy", "SatisfiedBy", "DeactivatedBy", "VerifiedBy", "UploaderId", "StoragePath",
            "CompletedByActor", "IsStalled", "OverdueBy", "PriorityRank", "NextStageGate", "UpcomingConditions"
        ];

        var offending = ReachableTypes(typeof(EngagementSummaryData))
            .Where(t => t != typeof(StaffSummaryDetails))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => forbidden.Any(f => name.EndsWith("." + f, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void Aggregate_DoesNotCopyInternalValuesFromItsInputs()
    {
        var data = EngagementSummaryAggregator.Aggregate(Scenario("in-progress"));
        var everything = string.Join("|", Strings(data));

        Assert.DoesNotContain("tenant-a", everything);
        Assert.DoesNotContain("client-1", everything);
        Assert.DoesNotContain("staff-1", everything);
        Assert.DoesNotContain("Internal review of risk", everything); // internal-only task title
    }

    // ---------------------------------------------------------------- scenarios

    public static EngagementSummaryInputs Scenario(string name) => name switch
    {
        "empty" => Inputs(EngagementStatus.Started, stage: 1, [], [], [], primary: null, pending: []),

        "in-progress" => Inputs(EngagementStatus.Started, stage: 2,
            actions:
            [
                Action("Provide passport", ClientActionType.KycDocument, ClientActionStatus.Pending, deadline: Oct(15)),
                Action("Upload bank statement", ClientActionType.DocumentUpload, ClientActionStatus.Pending, deadline: Oct(10)),
                Action("Sign engagement letter", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(3)),
                Action("Confirm contact details", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(4)),
                Action("Internal review of risk", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(6), internalOnly: true),
                Action("Old proof of address", ClientActionType.ProofOfAddress, ClientActionStatus.Cancelled)
            ],
            conditions:
            [
                Condition(ConditionType.Approval, ConditionStatus.Satisfied, "Approved", due: Oct(8), satisfied: Oct(5)),
                Condition(ConditionType.Payment, ConditionStatus.Pending, null, due: Oct(20), amount: 2500m)
            ],
            documents:
            [
                Doc("Passport", "Compliant", "Verified"),
                Doc("BankStatement", "Compliant", "Pending")
            ],
            primary: Safe("Provide passport", ClientActionStatus.Pending, Oct(15), overdue: false, description: "Clear photo of the photo page"),
            pending: [Safe("Upload bank statement", ClientActionStatus.Pending, Oct(10), overdue: true)]),

        "closed" => Inputs(EngagementStatus.Closed, stage: 5,
            actions:
            [
                Action("Provide passport", ClientActionType.KycDocument, ClientActionStatus.Completed, completed: Oct(2)),
                Action("Final sign-off", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(10))
            ],
            conditions:
            [
                Condition(ConditionType.Approval, ConditionStatus.Satisfied, "Approved", due: null, satisfied: Oct(4)),
                Condition(ConditionType.Payment, ConditionStatus.Satisfied, null, due: Oct(9), amount: 100m, satisfied: Oct(9))
            ],
            documents: [Doc("Passport", "Compliant", "Verified")],
            primary: null, pending: [], closedAt: Oct(11)),

        "rejected-docs" => Inputs(EngagementStatus.Started, stage: 2,
            actions:
            [
                Action("Passport", ClientActionType.KycDocument, ClientActionStatus.Rejected),
                Action("Proof of address", ClientActionType.ProofOfAddress, ClientActionStatus.Uploaded),
                Action("Bank statement", ClientActionType.DocumentUpload, ClientActionStatus.Uploaded)
            ],
            conditions: [],
            documents:
            [
                Doc("Passport", "Compliant", "Rejected", rejection: null, verificationReason: "Photo is blurred"),
                Doc("ProofOfAddress", "Rejected", "Unverified", rejection: "Document is expired"),
                Doc("BankStatement", "Pending", "Unverified")
            ],
            primary: Safe("Passport", ClientActionStatus.Rejected, null, overdue: false, rejection: "Photo is blurred"),
            pending: []),

        "overdue-payment" => Inputs(EngagementStatus.Started, stage: 3, [],
            conditions:
            [
                Condition(ConditionType.Payment, ConditionStatus.Pending, null, due: Oct(1), amount: 750m),
                Condition(ConditionType.Approval, ConditionStatus.Pending, "Pending", due: null)
            ],
            documents: [], primary: null, pending: []),

        "no-conditions" => Inputs(EngagementStatus.Started, stage: 2,
            actions:
            [
                Action("Provide passport", ClientActionType.KycDocument, ClientActionStatus.Uploaded),
                Action("Confirm contact details", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(4)),
                Action("Answer questionnaire", ClientActionType.CustomTask, ClientActionStatus.Pending)
            ],
            conditions: [],
            documents: [Doc("Passport", "Compliant", "Pending")],
            primary: Safe("Answer questionnaire", ClientActionStatus.Pending, null, overdue: false),
            pending: []),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };

    private static EngagementSummaryInputs Inputs(
        EngagementStatus status, int stage,
        IReadOnlyList<ClientActionResponseDto> actions,
        IReadOnlyList<ClientSafeConditionWithCompletion> conditions,
        IReadOnlyList<DocumentSummaryDto> documents,
        ClientSafeActionDto? primary,
        List<ClientSafeActionDto> pending,
        DateTime? closedAt = null)
    {
        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(), TenantId = "tenant-a", ClientId = "client-1", StaffId = "staff-1",
            Status = status, Stage = (EngagementStage)(stage - 1), CreatedAt = Oct(1), ClosedAt = closedAt
        };
        var visible = actions.Where(a => !a.IsInternalOnly && a.Status != ClientActionStatus.Cancelled).ToList();
        var done = visible.Count(a => a.Status == ClientActionStatus.Completed);

        return new EngagementSummaryInputs
        {
            Engagement = engagement,
            Dashboard = new ClientPortalDashboardDto
            {
                EngagementId = engagement.EngagementId,
                Status = status.ToString(),
                CreatedAt = engagement.CreatedAt,
                CurrentStageNumber = stage,
                CurrentStageName = $"Stage {stage}",
                ConditionDescription = "Live status line",
                ProgressPercentage = status == EngagementStatus.Closed ? 100 : visible.Count == 0 ? 0 : done * 100 / visible.Count,
                CompletedTasksCount = done,
                TotalTasksCount = visible.Count,
                PrimaryNextAction = primary,
                PendingActions = pending,
                Stages = Enumerable.Range(1, 5).Select(n => new ClientPortalStageDto
                {
                    StageNumber = n, Name = $"Stage {n}",
                    Status = n < stage ? "Completed" : n == stage ? "Current" : "Upcoming"
                }).ToList()
            },
            ClientVisibleActions = actions,
            Conditions = conditions,
            Documents = documents,
            Audience = EngagementSummaryAudience.Client,
            PreparedFor = "client@example.com",
            Now = Now
        };
    }

    private static ClientActionResponseDto Action(
        string title, string type, string status, DateTime? deadline = null, DateTime? completed = null, bool internalOnly = false) => new()
    {
        ActionId = Guid.NewGuid(), TenantId = "tenant-a", Title = title, Type = type, Status = status, StageNumber = 2,
        DeadlineUtc = deadline, CompletedAt = completed, IsInternalOnly = internalOnly, AssignedToRole = "Client",
        CompletedByActor = completed.HasValue ? "staff-1" : null
    };

    private static ClientSafeActionDto Safe(
        string title, string status, DateTime? deadline, bool overdue, string? description = null, string? rejection = null) => new()
    {
        ActionId = Guid.NewGuid(), Title = title, Type = ClientActionType.KycDocument, Status = status, StageNumber = 2,
        DeadlineUtc = deadline, IsOverdue = overdue, Description = description, RejectionReason = rejection
    };

    private static ClientSafeConditionWithCompletion Condition(
        string type, string status, string? approvalStatus, DateTime? due, decimal? amount = null, DateTime? satisfied = null) => new(
        new ClientSafeConditionDto
        {
            ConditionId = Guid.NewGuid(), Title = $"{type} condition", Type = type, Status = status, ApprovalStatus = approvalStatus,
            RequiredBeforeStage = nameof(EngagementStage.Execution), DueDateUtc = due, Amount = amount,
            Currency = amount.HasValue ? "LKR" : null, PaymentType = amount.HasValue ? "Upfront" : null
        },
        satisfied);

    private static DocumentSummaryDto Doc(
        string type, string compliance, string verification, string? rejection = null, string? verificationReason = null, bool deleted = false) => new()
    {
        DocumentId = Guid.NewGuid(), Type = type, ComplianceStatus = compliance, VerificationStatus = verification,
        RejectionReason = rejection, VerificationReason = verificationReason, IsDeleted = deleted,
        FileName = $"{type}.pdf", UploadedAt = Oct(2)
    };

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<Type> ReachableTypes(Type root)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([root]);
        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type)) continue;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (propertyType.IsGenericType) propertyType = propertyType.GetGenericArguments()[0];
                if (propertyType.Namespace == typeof(EngagementSummaryData).Namespace) queue.Enqueue(propertyType);
            }
        }
        return seen;
    }

    private static IEnumerable<string> Strings(object? value)
    {
        switch (value)
        {
            case null: yield break;
            case string s: yield return s; yield break;
            case System.Collections.IEnumerable list:
                foreach (var item in list) foreach (var s in Strings(item)) yield return s;
                yield break;
        }
        if (value.GetType().Namespace != typeof(EngagementSummaryData).Namespace) yield break;
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (var s in Strings(property.GetValue(value))) yield return s;
        }
    }

    private static void AssertAllDatesUtc(EngagementSummaryData data)
    {
        var dates = new List<DateTime?> { data.Overview.CreatedAtUtc, data.Overview.ClosedAtUtc, data.NextStep?.DueAtUtc };
        dates.AddRange(data.OutstandingActions.Select(a => a.DeadlineUtc));
        dates.AddRange(data.RecentlyCompletedActions.Select(a => a.CompletedAtUtc));
        dates.AddRange(data.Approvals.Concat(data.Payments).SelectMany(c => new[] { c.DueDateUtc, c.CompletedAtUtc }));
        dates.AddRange(data.Documents.Select(d => d.UploadedAtUtc));
        Assert.All(dates.Where(d => d.HasValue), d => Assert.Equal(DateTimeKind.Utc, d!.Value.Kind));
    }
}
