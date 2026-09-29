using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
using Custodian.Workflow.Services.Sla;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class NextActionRulesTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static Engagement CreateEngagement(
        EngagementStatus status = EngagementStatus.Started,
        EngagementStage stage = EngagementStage.Onboarding,
        Guid? id = null,
        EngagementStage? EngagementStage = null)
    {
        return new Engagement
        {
            EngagementId = id ?? Guid.NewGuid(),
            TenantId = "tenant-001",
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = status,
            Stage = EngagementStage ?? stage,
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        };
    }

    // =========================================================================
    // Rank 0: Engagement Closed, Cancelled, or Draft
    // =========================================================================

    [Fact]
    public void Rank0_WhenEngagementIsClosed_ReturnsNoPrimaryAndClosedState()
    {
        var engagement = CreateEngagement(EngagementStatus.Closed);
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[]
            {
                new ClientAction { Title = "Pending action", Status = ClientActionStatus.Pending, StageNumber = 1 }
            }
        };

        var staffResult = NextActionRules.Decide(inputs, NextActionView.Staff, _now);
        var clientResult = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.Equal(OverallState.Closed, staffResult.OverallState);
        Assert.Null(staffResult.PrimaryAction);
        Assert.Empty(staffResult.Blockers);

        Assert.Equal(OverallState.Closed, clientResult.OverallState);
        Assert.Null(clientResult.PrimaryAction);
        Assert.Empty(clientResult.Blockers);
    }

    [Fact]
    public void Rank0_WhenEngagementIsCancelled_ReturnsNoPrimaryAndClosedState()
    {
        var engagement = CreateEngagement(EngagementStatus.Cancelled);
        var inputs = new NextActionInputs { Engagement = engagement };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.Closed, result.OverallState);
        Assert.Null(result.PrimaryAction);
    }

    [Fact]
    public void Rank0_WhenEngagementIsDraft_StaffViewReturnsActivateEngagementTask()
    {
        var engagement = CreateEngagement(EngagementStatus.Draft, EngagementStage.Onboarding);
        var inputs = new NextActionInputs { Engagement = engagement };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.NotStarted, result.OverallState);
        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(NextActionKind.StaffTask, result.PrimaryAction.Kind);
        Assert.Equal(ResponsibleParty.Staff, result.PrimaryAction.ResponsibleParty);
        Assert.Equal("Activate engagement", result.PrimaryAction.Title);
        Assert.Equal(0, result.PrimaryAction.PriorityRank);
    }

    [Fact]
    public void Rank0_WhenEngagementIsDraft_ClientViewReturnsNullPrimary()
    {
        var engagement = CreateEngagement(EngagementStatus.Draft, EngagementStage.Onboarding);
        var inputs = new NextActionInputs { Engagement = engagement };

        var result = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.Equal(OverallState.NotStarted, result.OverallState);
        Assert.Null(result.PrimaryAction);
    }

    // =========================================================================
    // Rank 1: Overdue Client Items & Tie-breaking (AC5)
    // =========================================================================

    [Fact]
    public void Rank1_WhenClientActionIsOverdue_PromotedToRank1()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Overdue Document Upload",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(-2)
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(1, result.PrimaryAction.PriorityRank);
        Assert.True(result.PrimaryAction.IsOverdue);
        Assert.NotNull(result.PrimaryAction.OverdueBy);
        Assert.Equal(OverallState.ClientActionRequired, result.OverallState);
    }

    [Fact]
    public void Rank1_RejectedOverdueSortsBeforePendingOverdue()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var pendingAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Pending Overdue",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(-5)
        };
        var rejectedAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Rejected Overdue",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Rejected,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(-1)
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { pendingAction, rejectedAction }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal("Rejected Overdue", result.PrimaryAction.Title);
        Assert.Equal(1, result.PrimaryAction.PriorityRank);
        Assert.Single(result.Blockers);
        Assert.Equal("Pending Overdue", result.Blockers[0].Title);
    }

    // =========================================================================
    // Rank 2: Rejected Document / Evidence
    // =========================================================================

    [Fact]
    public void Rank2_WhenDocumentActionIsRejected_SelectedAtRank2()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Passport Re-upload",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Rejected,
            Description = "Blurry scan",
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(2, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.DocumentResubmission, result.PrimaryAction.Kind);
        Assert.Equal("Blurry scan", result.PrimaryAction.Reason);
    }

    // =========================================================================
    // Rank 3: Rejected or Requested Requirement
    // =========================================================================

    [Fact]
    public void Rank3_WhenRequirementIsRequestedOrRejected_SelectedAtRank3()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var req = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            Type = "CompanyRegistrationNumber",
            Status = RequirementStatus.Requested,
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Requirements = new[] { req }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(3, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.RequirementSubmission, result.PrimaryAction.Kind);
        Assert.Equal(ResponsibleParty.Client, result.PrimaryAction.ResponsibleParty);
    }

    // =========================================================================
    // Rank 4: Pending Document Upload Action
    // =========================================================================

    [Fact]
    public void Rank4_WhenActionIsPendingDocumentUpload_SelectedAtRank4()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Certificate of Incorporation",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(4, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.DocumentUpload, result.PrimaryAction.Kind);
    }

    // =========================================================================
    // Rank 5: Active Approval Condition
    // =========================================================================

    [Fact]
    public void Rank5_WhenActiveApprovalConditionGatesNextStage_SelectedAtRank5()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding); // Stage 1, next is DocumentCollection (Stage 2)
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Approval,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Partner Sign-off"
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            ActiveConditions = new[] { condition }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(5, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.ConditionApproval, result.PrimaryAction.Kind);
        Assert.Equal("Partner Sign-off", result.PrimaryAction.Title);
    }

    // =========================================================================
    // Rank 6: Active Payment Condition
    // =========================================================================

    [Fact]
    public void Rank6_WhenActivePaymentConditionGatesNextStage_SelectedAtRank6()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Payment,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Onboarding Retainer",
            Amount = 5000m,
            Currency = "USD"
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            ActiveConditions = new[] { condition }
        };

        var staffResult = NextActionRules.Decide(inputs, NextActionView.Staff, _now);
        var clientResult = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.NotNull(staffResult.PrimaryAction);
        Assert.Equal(6, staffResult.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.ConditionPayment, staffResult.PrimaryAction.Kind);
        Assert.Contains("Confirm payment", staffResult.PrimaryAction.Title);

        Assert.NotNull(clientResult.PrimaryAction);
        Assert.Equal("Payment due", clientResult.PrimaryAction.Title);
        Assert.Contains("5000.00 USD", clientResult.PrimaryAction.Reason);
    }

    // =========================================================================
    // Rank 7: Other Pending Client Tasks
    // =========================================================================

    [Fact]
    public void Rank7_WhenOtherPendingClientTaskExists_SelectedAtRank7()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Complete Questionnaire",
            Type = "CustomTask",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(7, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.ClientTask, result.PrimaryAction.Kind);
    }

    // =========================================================================
    // Rank 8: Overdue Staff Item
    // =========================================================================

    [Fact]
    public void Rank8_WhenStaffActionIsOverdue_SelectedAtRank8()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Review Compliance Filing",
            Type = "CustomTask",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Staff",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(-1)
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(8, result.PrimaryAction.PriorityRank);
        Assert.Equal(ResponsibleParty.Staff, result.PrimaryAction.ResponsibleParty);
    }

    // =========================================================================
    // Rank 9: Document Compliant but Unverified / Action in Uploaded
    // =========================================================================

    [Fact]
    public void Rank9_WhenActionIsUploaded_SelectedAtRank9()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Proof of Address",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Uploaded,
            // Evidence is client-assigned; the Uploaded state hands it to staff for verification.
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(9, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.DocumentVerification, result.PrimaryAction.Kind);
        Assert.Equal(ResponsibleParty.Staff, result.PrimaryAction.ResponsibleParty);
    }

    // =========================================================================
    // Rank 10: Submitted Requirement Awaiting Review
    // =========================================================================

    [Fact]
    public void Rank10_WhenRequirementIsSubmitted_SelectedAtRank10()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var req = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            Type = "TaxNumber",
            Status = RequirementStatus.Submitted,
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Requirements = new[] { req }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(10, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.RequirementReview, result.PrimaryAction.Kind);
        Assert.Equal(ResponsibleParty.Staff, result.PrimaryAction.ResponsibleParty);
    }

    // =========================================================================
    // Rank 11: Other Pending Staff Task
    // =========================================================================

    [Fact]
    public void Rank11_WhenOtherStaffTaskPending_SelectedAtRank11()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Assign Account Manager",
            Type = "CustomTask",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Staff",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(11, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.StaffTask, result.PrimaryAction.Kind);
        Assert.Equal(ResponsibleParty.Staff, result.PrimaryAction.ResponsibleParty);
    }

    // =========================================================================
    // Rank 12: Nothing Open and Gate Satisfied -> AdvanceStage
    // =========================================================================

    [Fact]
    public void Rank12_WhenNothingOpenAndGateSatisfied_StaffViewReturnsAdvanceStage()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.ReadyToAdvance, result.OverallState);
        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(12, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.AdvanceStage, result.PrimaryAction.Kind);
        Assert.Equal("Advance to DocumentCollection", result.PrimaryAction.Title);
    }

    [Fact]
    public void Rank12_WhenNothingOpenAndGateSatisfied_ClientViewReturnsNullPrimaryAndReadyToAdvance()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.Equal(OverallState.ReadyToAdvance, result.OverallState);
        Assert.Null(result.PrimaryAction);
        Assert.Empty(result.Blockers);
    }

    // =========================================================================
    // Rank 13: Nothing Open but Gate Blocked
    // =========================================================================

    [Fact]
    public void Rank13_WhenNothingOpenButGateBlocked_StaffViewReturnsResolveGateBlocker()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var gateBlocked = GateEvaluationResult.Blocked(
            "Missing mandatory KYC documents.",
            new[] { new GateRequirementResult("KYC", false, "Missing mandatory KYC documents.") });

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            NextStageGate = gateBlocked
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.AwaitingStaff, result.OverallState);
        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(13, result.PrimaryAction.PriorityRank);
        Assert.Equal(NextActionKind.StaffTask, result.PrimaryAction.Kind);
        Assert.Contains("Resolve gate blocker", result.PrimaryAction.Title);
    }

    [Fact]
    public void Rank13_WhenNothingOpenButGateBlocked_ClientViewShowsStaffReviewBlocker()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var gateBlocked = GateEvaluationResult.Blocked("Missing internal signoff.", Array.Empty<GateRequirementResult>());

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            NextStageGate = gateBlocked
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.Equal(OverallState.AwaitingStaff, result.OverallState);
        Assert.Null(result.PrimaryAction);
        Assert.Single(result.Blockers);
        Assert.Equal("Your submission is being reviewed by the Custodian team.", result.Blockers[0].Reason);
    }

    // =========================================================================
    // Rank 14: Closure Stage with Nothing Open -> AllComplete
    // =========================================================================

    [Fact]
    public void Rank14_WhenClosureStageAndNothingOpen_ReturnsAllComplete()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Closure);
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = Array.Empty<ClientAction>(),
            Requirements = Array.Empty<Requirement>()
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.AllComplete, result.OverallState);
        Assert.Null(result.PrimaryAction);
        Assert.Empty(result.Blockers);
    }

    // =========================================================================
    // Tie-Breakers
    // =========================================================================

    [Fact]
    public void TieBreakers_InsideSameRank_SortsByDueAtThenStageThenCreatedAt()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.DocumentCollection); // Stage 2
        var olderCurrentStageAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Task Due Earlier",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 2,
            DeadlineUtc = _now.UtcDateTime.AddDays(1),
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        };
        var laterCurrentStageAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Task Due Later",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 2,
            DeadlineUtc = _now.UtcDateTime.AddDays(3),
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        };
        var noDeadlineAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Task No Deadline",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 2,
            DeadlineUtc = null
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { noDeadlineAction, laterCurrentStageAction, olderCurrentStageAction }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal("Task Due Earlier", result.PrimaryAction!.Title);
        Assert.Equal(2, result.Blockers.Count);
        Assert.Equal("Task Due Later", result.Blockers[0].Title);
        Assert.Equal("Task No Deadline", result.Blockers[1].Title);
    }

    [Fact]
    public void TieBreakers_CurrentStageTakesPrecedenceOverEarlierStageLeftovers()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.DocumentCollection); // Stage 2
        var stage1Leftover = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Stage 1 Task",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        };
        var stage2Action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Stage 2 Task",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 2,
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { stage1Leftover, stage2Action }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal("Stage 2 Task", result.PrimaryAction!.Title);
        Assert.Single(result.Blockers);
        Assert.Equal("Stage 1 Task", result.Blockers[0].Title);
    }

    // =========================================================================
    // Acceptance Criteria (AC2, AC3, AC4, AC5, AC6)
    // =========================================================================

    [Fact]
    public void AC2_AC3_InactiveConditionsAreNeverRead()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var inactiveApproval = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Approval,
            Status = ConditionStatus.Pending,
            IsActive = false, // INACTIVE
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Inactive Approval"
        };
        var inactivePayment = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Payment,
            Status = ConditionStatus.Pending,
            IsActive = false, // INACTIVE
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Inactive Payment"
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            ActiveConditions = new[] { inactiveApproval, inactivePayment },
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        // Inactive conditions should not appear as primary or blockers
        Assert.NotEqual("Inactive Approval", result.PrimaryAction?.Title);
        Assert.NotEqual("Inactive Payment", result.PrimaryAction?.Title);
        Assert.DoesNotContain(result.Blockers, b => b.Title is "Inactive Approval" or "Inactive Payment");
        Assert.Equal(NextActionKind.AdvanceStage, result.PrimaryAction?.Kind);
    }

    [Fact]
    public void AC4_SatisfiedConditionsAreNeverBlockers()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var satisfiedCondition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Approval,
            Status = ConditionStatus.Satisfied, // SATISFIED
            IsActive = true,
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Satisfied Sign-off"
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            ActiveConditions = new[] { satisfiedCondition },
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotEqual("Satisfied Sign-off", result.PrimaryAction?.Title);
        Assert.Empty(result.Blockers);
        Assert.Equal(NextActionKind.AdvanceStage, result.PrimaryAction?.Kind);
    }

    [Fact]
    public void AC5_OverdueVsNotOverdueChangesRank()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var actionNotOverdue = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Doc Upload",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(2)
        };

        var notOverdueInputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { actionNotOverdue }
        };

        var notOverdueResult = NextActionRules.Decide(notOverdueInputs, NextActionView.Staff, _now);
        Assert.Equal(4, notOverdueResult.PrimaryAction!.PriorityRank); // Rank 4 (pending upload)

        // Make it overdue
        var actionOverdue = new ClientAction
        {
            ActionId = actionNotOverdue.ActionId,
            Title = "Doc Upload",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(-2)
        };

        var overdueInputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { actionOverdue }
        };

        var overdueResult = NextActionRules.Decide(overdueInputs, NextActionView.Staff, _now);
        Assert.Equal(1, overdueResult.PrimaryAction!.PriorityRank); // Rank 1 (promoted due to overdue)
    }

    [Fact]
    public void AC6_ReturnsExactlyOnePrimaryPlusBlockersWithNoDuplicates()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action1 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Action 1",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };
        var action2 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Action 2",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };
        var action3 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Action 3",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action1, action2, action3 }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.NotNull(result.PrimaryAction);
        Assert.Equal(2, result.Blockers.Count);
        // Verify no duplicate between primary and blockers
        Assert.DoesNotContain(result.Blockers, b => b.ActionId == result.PrimaryAction.ActionId);
    }

    // =========================================================================
    // Client View Constraints
    // =========================================================================

    [Fact]
    public void ClientView_NeverContainsStaffPrimaryOrInternalActions()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var internalAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Internal Risk Review",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            IsInternalOnly = true, // INTERNAL ONLY
            StageNumber = 1
        };
        var staffAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Staff KYC Check",
            Status = ClientActionStatus.Uploaded,
            AssignedToRole = "Staff",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { internalAction, staffAction }
        };

        var clientResult = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        // Internal action must not appear
        Assert.NotEqual("Internal Risk Review", clientResult.PrimaryAction?.Title);
        Assert.DoesNotContain(clientResult.Blockers, b => b.Title == "Internal Risk Review");

        // Staff action cannot be primary in client view; summarized as reviewer blocker
        Assert.Null(clientResult.PrimaryAction);
        Assert.Single(clientResult.Blockers);
        Assert.Equal("Your submission is being reviewed by the Custodian team.", clientResult.Blockers[0].Reason);
        Assert.Equal(OverallState.AwaitingStaff, clientResult.OverallState);
    }

    [Fact]
    public void ClientView_StripsStaffMetadataFields()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var clientAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Client Form",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            DeadlineUtc = _now.UtcDateTime.AddDays(2)
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { clientAction },
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var clientResult = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.NotNull(clientResult.PrimaryAction);
        Assert.Null(clientResult.PrimaryAction.SourceId);
        Assert.Null(clientResult.PrimaryAction.OverdueBy);
        Assert.Equal(0, clientResult.PrimaryAction.PriorityRank);
        Assert.Null(clientResult.NextStageGate);
    }

    // =========================================================================
    // Dependency Failure: Documents Unavailable
    // =========================================================================

    [Fact]
    public void DependencyFailure_WhenDocumentsUnavailable_SetsBlockedExternalAndNeverClaimsReadyToAdvance()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = Array.Empty<ClientAction>(),
            IsDocumentsUnavailable = true
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.BlockedExternal, result.OverallState);
        Assert.Null(result.PrimaryAction);
        Assert.Single(result.Blockers);
        Assert.Equal(NextActionKind.Unavailable, result.Blockers[0].Kind);
        Assert.Equal("Document status temporarily unavailable", result.Blockers[0].Reason);
    }

    [Fact]
    public void DependencyFailure_WhenDocumentsUnavailable_StillReturnsClientItemsRanks1To7()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var clientAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Client Form",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { clientAction },
            IsDocumentsUnavailable = true
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(OverallState.BlockedExternal, result.OverallState);
        Assert.NotNull(result.PrimaryAction);
        Assert.Equal("Client Form", result.PrimaryAction.Title);
        Assert.Contains(result.Blockers, b => b.Kind == NextActionKind.Unavailable);
    }

    // =========================================================================
    // Determinism
    // =========================================================================

    [Fact]
    public void Determinism_CallingDecideTwiceWithIdenticalInputsProducesIdenticalResults()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var action1 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Action 1",
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };
        var action2 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Action 2",
            Status = ClientActionStatus.Uploaded,
            AssignedToRole = "Staff",
            StageNumber = 1
        };

        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action1, action2 }
        };

        var run1 = NextActionRules.Decide(inputs, NextActionView.Staff, _now);
        var run2 = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(run1.OverallState, run2.OverallState);
        Assert.Equal(run1.PrimaryAction?.Title, run2.PrimaryAction?.Title);
        Assert.Equal(run1.PrimaryAction?.PriorityRank, run2.PrimaryAction?.PriorityRank);
        Assert.Equal(run1.Blockers.Count, run2.Blockers.Count);
        for (int i = 0; i < run1.Blockers.Count; i++)
        {
            Assert.Equal(run1.Blockers[i].Title, run2.Blockers[i].Title);
            Assert.Equal(run1.Blockers[i].PriorityRank, run2.Blockers[i].PriorityRank);
        }
    }

    // =========================================================================
    // Correctness fixes (review A1–A8)
    // =========================================================================

    [Fact]
    public void A1_ClientAssignedUploadedEvidence_IsStaffVerificationAtRank9_AndNotClientPrimary()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.DocumentCollection);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Passport",
            Type = ClientActionType.KycDocument,
            Status = ClientActionStatus.Uploaded,
            AssignedToRole = "Client",
            StageNumber = 2
        };
        var inputs = new NextActionInputs { Engagement = engagement, Actions = new[] { action } };

        var staff = NextActionRules.Decide(inputs, NextActionView.Staff, _now);
        var client = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.Equal(9, staff.PrimaryAction!.PriorityRank);
        Assert.Equal(ResponsibleParty.Staff, staff.PrimaryAction.ResponsibleParty);
        Assert.Equal(action.ActionId, staff.PrimaryAction.ActionId);
        Assert.Equal(OverallState.AwaitingStaff, staff.OverallState);

        Assert.Null(client.PrimaryAction);
        Assert.Equal(OverallState.AwaitingStaff, client.OverallState);
        Assert.Contains(client.Blockers, b => b.Reason == "Your submission is being reviewed by the Custodian team.");
    }

    [Fact]
    public void A1_PendingActionWithCompliantUnverifiedLinkedDocument_IsStaffRank9()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.DocumentCollection);
        var docId = Guid.NewGuid();
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Passport",
            Type = ClientActionType.KycDocument,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 2,
            LinkedDocumentId = docId
        };
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { action },
            Documents = new[] { new DocumentSummaryDto { DocumentId = docId, ComplianceStatus = "Compliant", VerificationStatus = "Unverified" } }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(9, result.PrimaryAction!.PriorityRank);
        Assert.Equal(docId, result.PrimaryAction.SourceId);
    }

    [Fact]
    public void A4_OverdueClientUploadedEvidence_IsStaffRank8_NotClientRank1()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.DocumentCollection);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Passport",
            Type = ClientActionType.KycDocument,
            Status = ClientActionStatus.Uploaded,
            AssignedToRole = "Client",
            StageNumber = 2,
            DeadlineUtc = _now.UtcDateTime.AddDays(-2)
        };
        var inputs = new NextActionInputs { Engagement = engagement, Actions = new[] { action } };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(8, result.PrimaryAction!.PriorityRank);
        Assert.Equal(ResponsibleParty.Staff, result.PrimaryAction.ResponsibleParty);
        Assert.Equal(NextActionKind.DocumentVerification, result.PrimaryAction.Kind);
        Assert.True(result.PrimaryAction.IsOverdue);
    }

    [Theory]
    [InlineData(ClientActionType.KycDocument)]
    [InlineData(ClientActionType.SignAgreement)]
    [InlineData(ClientActionType.ProofOfAddress)]
    [InlineData(ClientActionType.DocumentUpload)]
    public void A7_PendingEvidenceTypes_AreRank4DocumentUpload(string type)
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.DocumentCollection);
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Evidence",
            Type = type,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 2
        };
        var inputs = new NextActionInputs { Engagement = engagement, Actions = new[] { action } };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(4, result.PrimaryAction!.PriorityRank);
        Assert.Equal(NextActionKind.DocumentUpload, result.PrimaryAction.Kind);
    }

    [Fact]
    public void A2_ConditionWithLinkedAction_ProducesSingleItemCarryingActionId()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Verification);
        var conditionId = Guid.NewGuid();
        var linkedAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Scope approval",
            Type = ClientActionType.Approval,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 3,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId
        };
        var condition = new EngagementCondition
        {
            ConditionId = conditionId,
            Type = ConditionType.Approval,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Scope approval"
        };
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { linkedAction },
            ActiveConditions = new[] { condition }
        };

        var staff = NextActionRules.Decide(inputs, NextActionView.Staff, _now);
        var client = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        Assert.Equal(5, staff.PrimaryAction!.PriorityRank);
        Assert.Equal(linkedAction.ActionId, staff.PrimaryAction.ActionId);
        Assert.Equal(conditionId, staff.PrimaryAction.SourceId);
        Assert.Empty(staff.Blockers);
        Assert.DoesNotContain(staff.Blockers, b => b.PriorityRank == 7);

        Assert.Equal(linkedAction.ActionId, client.PrimaryAction!.ActionId);
        Assert.Empty(client.Blockers);
    }

    [Fact]
    public void A2_AC4_SatisfiedConditionWithStillPendingLinkedAction_IsNotABlocker()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Verification);
        var conditionId = Guid.NewGuid();
        var linkedAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Scope approval",
            Type = ClientActionType.Approval,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 3,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId
        };
        var satisfied = new EngagementCondition
        {
            ConditionId = conditionId,
            Type = ConditionType.Approval,
            Status = ConditionStatus.Satisfied,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Scope approval"
        };
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { linkedAction },
            ActiveConditions = new[] { satisfied },
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(NextActionKind.AdvanceStage, result.PrimaryAction!.Kind);
        Assert.Equal(OverallState.ReadyToAdvance, result.OverallState);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void A3_RejectedApproval_NeverExposesInternalNote()
    {
        const string internalNote = "Staff only: client disputed the fee";
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Verification);
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Approval,
            Status = ConditionStatus.Rejected,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Scope approval",
            InternalNote = internalNote
        };
        var inputs = new NextActionInputs { Engagement = engagement, ActiveConditions = new[] { condition } };

        foreach (var view in new[] { NextActionView.Staff, NextActionView.Client })
        {
            var result = NextActionRules.Decide(inputs, view, _now);
            var items = new[] { result.PrimaryAction! }.Concat(result.Blockers);

            Assert.Equal("Approval was declined.", result.PrimaryAction!.Reason);
            Assert.DoesNotContain(items, i => i.Reason.Contains(internalNote) || i.Title.Contains(internalNote));
        }
    }

    [Fact]
    public void A5_RequirementWhoseMirroredActionIsOverdue_IsPromotedToRank1()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var requirementId = Guid.NewGuid();
        var requirement = new Requirement
        {
            RequirementId = requirementId,
            Type = "TaxId",
            Status = RequirementStatus.Requested,
            StageNumber = 1
        };
        var mirrored = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Provide Tax ID",
            Type = ClientActionType.Requirement,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1,
            LinkedRequirementId = requirementId,
            DeadlineUtc = _now.UtcDateTime.AddDays(-1)
        };
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            Requirements = new[] { requirement },
            Actions = new[] { mirrored }
        };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(1, result.PrimaryAction!.PriorityRank);
        Assert.Equal(NextActionKind.RequirementSubmission, result.PrimaryAction.Kind);
        Assert.True(result.PrimaryAction.IsOverdue);
        Assert.Equal(mirrored.ActionId, result.PrimaryAction.ActionId);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void A8_ConditionsUnavailable_SetsBlockedExternal_KeepsClientItems_NeverReadyToAdvance()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var clientTask = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            Title = "Client Form",
            Type = ClientActionType.CustomTask,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        };

        var withItems = NextActionRules.Decide(new NextActionInputs
        {
            Engagement = engagement,
            Actions = new[] { clientTask },
            IsConditionsUnavailable = true
        }, NextActionView.Client, _now);

        Assert.Equal(OverallState.BlockedExternal, withItems.OverallState);
        Assert.Equal("Client Form", withItems.PrimaryAction!.Title);
        Assert.Contains(withItems.Blockers, b => b.Reason == "Condition status temporarily unavailable");

        var nothingOpen = NextActionRules.Decide(new NextActionInputs
        {
            Engagement = engagement,
            IsConditionsUnavailable = true,
            NextStageGate = GateEvaluationResult.Satisfied()
        }, NextActionView.Staff, _now);

        Assert.Equal(OverallState.BlockedExternal, nothingOpen.OverallState);
        Assert.Null(nothingOpen.PrimaryAction);
    }

    // =========================================================================
    // A12: later-stage conditions are informational only (staff view)
    // =========================================================================

    [Fact]
    public void A12_LaterStageCondition_IsListedInUpcomingConditionsOnly_ForStaff()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding); // next stage: DocumentCollection
        var later = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Payment,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Milestone payment",
            Amount = 1000m,
            Currency = "USD"
        };
        var satisfiedLater = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            Type = ConditionType.Approval,
            Status = ConditionStatus.Satisfied,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Closure,
            Title = "Final sign-off"
        };
        var inputs = new NextActionInputs
        {
            Engagement = engagement,
            ActiveConditions = new[] { later, satisfiedLater },
            NextStageGate = GateEvaluationResult.Satisfied()
        };

        var staff = NextActionRules.Decide(inputs, NextActionView.Staff, _now);
        var client = NextActionRules.Decide(inputs, NextActionView.Client, _now);

        // Informational only: does not block, does not change state
        Assert.Equal(OverallState.ReadyToAdvance, staff.OverallState);
        Assert.Equal(NextActionKind.AdvanceStage, staff.PrimaryAction!.Kind);
        Assert.Empty(staff.Blockers);

        var upcoming = Assert.Single(staff.UpcomingConditions);
        Assert.Equal("Milestone payment", upcoming.Title);
        Assert.Equal(NextActionKind.ConditionPayment, upcoming.Kind);
        Assert.Equal(later.ConditionId, upcoming.SourceId);

        Assert.Empty(client.UpcomingConditions);
        Assert.Equal(OverallState.ReadyToAdvance, client.OverallState);
    }

    [Fact]
    public void A12_UpcomingConditions_AreOrderedByRequiredStageThenCreatedAt()
    {
        var engagement = CreateEngagement(EngagementStage: EngagementStage.Onboarding);
        var closure = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(), Type = ConditionType.Approval, Status = ConditionStatus.Pending, IsActive = true,
            RequiredBeforeStage = EngagementStage.Closure, Title = "Closure approval", CreatedAt = _now.UtcDateTime.AddDays(-5)
        };
        var execution = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(), Type = ConditionType.Approval, Status = ConditionStatus.Pending, IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution, Title = "Execution approval", CreatedAt = _now.UtcDateTime.AddDays(-1)
        };
        var inputs = new NextActionInputs { Engagement = engagement, ActiveConditions = new[] { closure, execution } };

        var result = NextActionRules.Decide(inputs, NextActionView.Staff, _now);

        Assert.Equal(new[] { "Execution approval", "Closure approval" }, result.UpcomingConditions.Select(u => u.Title));
    }
}
