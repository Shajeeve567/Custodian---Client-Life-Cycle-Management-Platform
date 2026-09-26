using Custodian.Shared.Contracts;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.NextAction;
using Custodian.Workflow.Services.Sla;
using Custodian.Workflow.Services.Stall;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientPortalServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    private static IClientPortalService CreatePortalService(
        WorkflowDbContext db,
        INextActionService? nextActionService = null,
        IReadOnlyList<EngagementCondition>? activeConditions = null)
    {
        if (nextActionService == null)
        {
            var mockConditionService = new Mock<IConditionService>();
            mockConditionService.Setup(c => c.GetActiveConditionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(activeConditions ?? new List<EngagementCondition>());

            var mockDocClient = new Mock<IDocumentComplianceClient>();
            mockDocClient.Setup(d => d.GetDocumentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<DocumentSummaryDto>());

            var slaCalc = new DefaultSlaCalculator();
            var mockGateEvaluator = new Mock<IGateEvaluator>();
            var mockStallService = new Mock<IStallService>();
            mockStallService.Setup(s => s.GetStallStatusAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            nextActionService = new NextActionService(
                db,
                mockConditionService.Object,
                mockDocClient.Object,
                slaCalc,
                mockGateEvaluator.Object,
                mockStallService.Object,
                TimeProvider.System,
                NullLogger<NextActionService>.Instance);
        }

        return new ClientPortalService(db, nextActionService);
    }

    [Fact]
    public async Task GetDashboard_CalculatesProgressBasedOnTotalCompletedTasks()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        // Add 4 client tasks (2 completed, 2 pending) + 1 internal task (which shouldn't count in client progress)
        db.ClientActions.AddRange(
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 1", Status = ClientActionStatus.Completed, StageNumber = 1, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 2", Status = ClientActionStatus.Completed, StageNumber = 1, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 3", Status = ClientActionStatus.Pending, StageNumber = 2, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Task 4", Status = ClientActionStatus.Pending, StageNumber = 2, IsInternalOnly = false },
            new ClientAction { EngagementId = engagementId, TenantId = tenantId, Title = "Staff Audit", Status = ClientActionStatus.Pending, StageNumber = 2, IsInternalOnly = true }
        );
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: 2 completed out of 4 client tasks = 50%
        Assert.NotNull(dashboard);
        Assert.Equal(4, dashboard.TotalTasksCount);
        Assert.Equal(2, dashboard.CompletedTasksCount);
        Assert.Equal(50, dashboard.ProgressPercentage);
    }

    [Fact]
    public async Task GetDashboard_SelectsPrimaryNextAction_BasedOnActiveOnboardingStage()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection
        });

        // Stage 1 completed
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Stage 1 Intake Form",
            Status = ClientActionStatus.Completed,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });

        // Stage 2 has two pending tasks (one with earlier deadline)
        var earlyDeadline = DateTime.UtcNow.AddDays(1);
        var laterDeadline = DateTime.UtcNow.AddDays(5);

        db.ClientActions.AddRange(
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Corporate Charter Upload",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = laterDeadline,
                IsInternalOnly = false,
                AssignedToRole = "Client"
            },
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Proof of Address Upload",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                DeadlineUtc = earlyDeadline,
                IsInternalOnly = false,
                AssignedToRole = "Client"
            },
            // Stage 3 also has a pending task, but Stage 2 must take priority!
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Retainer Escrow Deposit",
                Status = ClientActionStatus.Pending,
                StageNumber = 3,
                DeadlineUtc = DateTime.UtcNow.AddHours(2), // earlier deadline, but in a later stage!
                IsInternalOnly = false,
                AssignedToRole = "Client"
            }
        );
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: Primary action must be from Stage 2 (the current stage), with earliest deadline ("Proof of Address Upload")
        Assert.NotNull(dashboard);
        Assert.Equal(2, dashboard.CurrentStageNumber);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal("Proof of Address Upload", dashboard.PrimaryNextAction.Title);
        Assert.Equal(2, dashboard.PrimaryNextAction.StageNumber);
        Assert.Equal("ActionRequired", dashboard.ConditionStatus);
    }

    [Fact]
    public async Task GetDashboard_WhenActionUploaded_SetsConditionStatusToUnderReview()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        // Action was uploaded by client, awaiting staff review
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Passport Identity Copy",
            Status = ClientActionStatus.Uploaded,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Null(dashboard.PrimaryNextAction); // No action required by client right now
        Assert.Equal("UnderReview", dashboard.ConditionStatus);
    }

    [Fact]
    public async Task GetDashboard_DifferentClient_EnforcesOwnershipAndReturnsNull()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var legitimateClient = "client-owner-123";
        var attackerClient = "client-attacker-999";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = legitimateClient,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act: Attempt to access engagement owned by client-owner-123 using client-attacker-999
        var result = await service.GetDashboardForEngagementAsync(engagementId, tenantId, attackerClient);

        // Assert: Must be rejected (returns null)
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveDashboardForClient_ResolvesActiveEngagementAutomatically()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var tenantId = "tenant-test";
        var clientId = "client-active-user";

        // Older closed engagement
        db.Engagements.Add(new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Closed,
            Stage = EngagementStage.Closure,
            CreatedAt = DateTime.UtcNow.AddMonths(-2)
        });

        // Active ongoing engagement
        var activeEngagementId = Guid.NewGuid();
        db.Engagements.Add(new Engagement
        {
            EngagementId = activeEngagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act: Client doesn't pass any engagement GUID
        var dashboard = await service.GetActiveDashboardForClientAsync(tenantId, clientId);

        // Assert: Automatically resolves the active "Started" engagement
        Assert.NotNull(dashboard);
        Assert.Equal(activeEngagementId, dashboard.EngagementId);
    }

    [Fact]
    public async Task GetDashboard_RejectedActionWithReason_MapsRejectionReasonToSafeAction()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";
        var expectedReason = "Document exceeds maximum allowable age of 90 days (issued 120 days ago).";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection
        });

        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Utility Bill",
            Status = ClientActionStatus.Rejected,
            StageNumber = 2,
            IsInternalOnly = false,
            AssignedToRole = "Client",
            SourceMetadata = $"{{\"documentId\":\"{Guid.NewGuid()}\",\"complianceStatus\":\"Rejected\",\"rejectionReason\":\"{expectedReason}\"}}"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal(ClientActionStatus.Rejected, dashboard.PrimaryNextAction.Status);
        Assert.Equal(expectedReason, dashboard.PrimaryNextAction.RejectionReason);
    }

    [Fact]
    public async Task GetDashboard_AutoCompliantUploadedAction_KeepsStageInUnderReview_AndBlocksGateAdvance()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection
        });

        // Stage 1 completed
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Stage 1 Intake",
            Status = ClientActionStatus.Completed,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });

        // Stage 2 has an auto-compliant uploaded document awaiting human verification
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Articles of Incorporation",
            Status = ClientActionStatus.Uploaded,
            StageNumber = 2,
            IsInternalOnly = false,
            AssignedToRole = "Client",
            SourceMetadata = $"{{\"documentId\":\"{Guid.NewGuid()}\",\"complianceStatus\":\"Compliant\",\"verificationStatus\":\"Pending\"}}"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert:
        // 1. Stage gate does not advance past Stage 2
        Assert.NotNull(dashboard);
        Assert.Equal(2, dashboard.CurrentStageNumber);
        // 2. Condition status is "UnderReview"
        Assert.Equal("UnderReview", dashboard.ConditionStatus);
        Assert.Contains("verified by the custodian team", dashboard.ConditionDescription);
        // 3. Progress percentage is 50% (1 of 2 completed)
        Assert.Equal(50, dashboard.ProgressPercentage);
    }

    [Fact]
    public async Task GetDashboard_VerifiedAction_AdvancesStageGate_AndReflectsProgress()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Verification
        });

        // Stage 1 completed
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Stage 1 Intake",
            Status = ClientActionStatus.Completed,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });

        // Stage 2 human-verified document -> Completed
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Articles of Incorporation",
            Status = ClientActionStatus.Completed,
            StageNumber = 2,
            IsInternalOnly = false,
            AssignedToRole = "Client",
            SourceMetadata = $"{{\"documentId\":\"{Guid.NewGuid()}\",\"complianceStatus\":\"Compliant\",\"verificationStatus\":\"Verified\"}}"
        });

        // Stage 3 action pending
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Executive Sign-Off",
            Status = ClientActionStatus.Pending,
            StageNumber = 3,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: Gate advances to Stage 3 now that Stage 2 human verification completed
        Assert.NotNull(dashboard);
        Assert.Equal(3, dashboard.CurrentStageNumber);
        Assert.Equal("ActionRequired", dashboard.ConditionStatus);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal("Executive Sign-Off", dashboard.PrimaryNextAction.Title);
    }

    [Fact]
    public async Task GetDashboard_RejectedVerification_ExposesStaffVerificationReasonInSafeDto()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";
        var staffReason = "Official stamp is blurred and certificate registry ID is illegible.";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection
        });

        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Certificate of Good Standing",
            Status = ClientActionStatus.Rejected,
            StageNumber = 2,
            IsInternalOnly = false,
            AssignedToRole = "Client",
            SourceMetadata = $"{{\"documentId\":\"{Guid.NewGuid()}\",\"complianceStatus\":\"Compliant\",\"verificationStatus\":\"Rejected\",\"verificationReason\":\"{staffReason}\"}}"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Equal("RevisionRequired", dashboard.ConditionStatus);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal(ClientActionStatus.Rejected, dashboard.PrimaryNextAction.Status);
        Assert.Equal(staffReason, dashboard.PrimaryNextAction.RejectionReason);
        Assert.Equal(DocumentVerificationStatus.Rejected, dashboard.PrimaryNextAction.VerificationStatus);
    }

    [Fact]
    public async Task GetDashboard_WhenNoActionsExist_AutoSeedsLifecycleDefaultActions()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: 19-N3 Pure read-only query — zero actions seeded into DB on read
        Assert.NotNull(dashboard);
        Assert.Equal(1, dashboard.CurrentStageNumber);
        Assert.Null(dashboard.PrimaryNextAction);

        // Verify zero actions were persisted to database
        var persistedActions = await db.ClientActions
            .Where(a => a.EngagementId == engagementId)
            .ToListAsync();
        Assert.Empty(persistedActions);
    }

    [Fact]
    public async Task GetDashboard_WhenStaffAdvancesEngagementStage_SynchronizesCurrentStageAndPrimaryAction()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        // Staff moves engagement to Stage 2 (DocumentCollection)
        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection
        });

        // Stage 1 task is still in Pending
        db.ClientActions.AddRange(
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Old Stage 1 Intake",
                Status = ClientActionStatus.Pending,
                StageNumber = 1,
                IsInternalOnly = false,
                AssignedToRole = "Client"
            },
            new ClientAction
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Upload Identity Proof",
                Status = ClientActionStatus.Pending,
                StageNumber = 2,
                IsInternalOnly = false,
                AssignedToRole = "Client",
                DeadlineUtc = DateTime.UtcNow.AddDays(5)
            }
        );
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.Equal(2, dashboard.CurrentStageNumber);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal("Upload Identity Proof", dashboard.PrimaryNextAction.Title);
        Assert.Equal(2, dashboard.PrimaryNextAction.StageNumber);

        // 19-N3: Pure read-only query — previous stage pending action remains untouched in Pending status
        var stage1Action = await db.ClientActions
            .FirstAsync(a => a.EngagementId == engagementId && a.StageNumber == 1);
        Assert.Equal(ClientActionStatus.Pending, stage1Action.Status);
    }

    [Fact]
    public async Task GetDashboard_WhenOnlyStage1ActionsExist_SeedsMissingStages2Through5()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        // Add ONLY Stage 1 action (completed)
        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Existing Intake Form",
            Status = ClientActionStatus.Completed,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client",
            Source = "LifecycleDefault"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert: 19-N3 Pure read-only query — missing stages are NOT seeded into DB
        Assert.NotNull(dashboard);
        Assert.Equal(1, dashboard.CurrentStageNumber);

        var allActions = await db.ClientActions
            .Where(a => a.EngagementId == engagementId)
            .ToListAsync();
        Assert.Single(allActions);
        Assert.DoesNotContain(allActions, a => a.StageNumber > 1);
    }

    [Fact]
    public async Task GetActiveDashboardForClientAsync_PrioritizesLatestDraftOverOldClosedEngagement()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        var oldClosedId = Guid.NewGuid();
        var newDraftId = Guid.NewGuid();

        db.Engagements.AddRange(
            new Engagement
            {
                EngagementId = oldClosedId,
                TenantId = tenantId,
                ClientId = clientId,
                StaffId = "staff-1",
                Status = EngagementStatus.Closed,
                Stage = EngagementStage.Closure,
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            },
            new Engagement
            {
                EngagementId = newDraftId,
                TenantId = tenantId,
                ClientId = clientId,
                StaffId = "staff-1",
                Status = EngagementStatus.Draft,
                Stage = EngagementStage.DocumentCollection,
                CreatedAt = DateTime.UtcNow
            }
        );
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetActiveDashboardForClientAsync(tenantId, clientId);

        // Assert: Should resolve the active/draft engagement (Stage 2), not the old closed engagement!
        Assert.NotNull(dashboard);
        Assert.Equal(newDraftId, dashboard.EngagementId);
        Assert.Equal(2, dashboard.CurrentStageNumber);
    }

    [Fact]
    public async Task GetDashboard_PopulatesSourceTypeOnClientSafeActionDto()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var clientId = "client-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Submit Business Plan",
            StageNumber = 1,
            Status = ClientActionStatus.Pending,
            SourceType = ClientActionSourceType.Document,
            IsInternalOnly = false
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal(ClientActionSourceType.Document, dashboard.PrimaryNextAction.SourceType);
    }

    [Fact]
    public async Task GetDashboard_CancelledActions_ExcludedFromPendingAndDoNotBlockStageProgress()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var clientId = "client-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection // Stage 2
        });

        // Stage 1 action was cancelled (e.g. requirement waived)
        var cancelledActionStage1 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Waived Step",
            StageNumber = 1,
            Status = ClientActionStatus.Cancelled,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        };

        // Stage 2 active pending action
        var pendingActionStage2 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Incorporation Certificate",
            StageNumber = 2,
            Status = ClientActionStatus.Pending,
            SourceType = ClientActionSourceType.Document,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        };

        db.ClientActions.AddRange(cancelledActionStage1, pendingActionStage2);
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        // Stage 1 cancelled action does NOT drag stage back to 1
        Assert.Equal(2, dashboard.CurrentStageNumber);

        // Primary next action is the stage 2 pending action, not the cancelled stage 1 action
        Assert.NotNull(dashboard.PrimaryNextAction);
        Assert.Equal(pendingActionStage2.ActionId, dashboard.PrimaryNextAction.ActionId);
        Assert.Equal(ClientActionStatus.Pending, dashboard.PrimaryNextAction.Status);

        // Cancelled action is not in pending actions either
        Assert.DoesNotContain(dashboard.PendingActions, a => a.ActionId == cancelledActionStage1.ActionId);
    }

    [Fact]
    public async Task GetDashboard_MultipleCalls_ProduceZeroSideEffectsAndIdenticalResults()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-idempotent";
        var clientId = "client-idempotent";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.DocumentCollection
        });

        var action1 = new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Proof of Address",
            Status = ClientActionStatus.Pending,
            StageNumber = 2,
            IsInternalOnly = false,
            AssignedToRole = "Client",
            DeadlineUtc = DateTime.UtcNow.AddDays(2)
        };
        db.ClientActions.Add(action1);
        await db.SaveChangesAsync();

        var initialActionCount = await db.ClientActions.CountAsync();
        var initialEngagement = await db.Engagements.AsNoTracking().FirstAsync(e => e.EngagementId == engagementId);

        var service = CreatePortalService(db);

        // Act: Execute 10 consecutive GET calls
        ClientPortalDashboardDto? firstResult = null;
        for (int i = 0; i < 10; i++)
        {
            var result = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);
            Assert.NotNull(result);

            if (firstResult == null)
            {
                firstResult = result;
            }
            else
            {
                Assert.Equal(firstResult.EngagementId, result.EngagementId);
                Assert.Equal(firstResult.CurrentStageNumber, result.CurrentStageNumber);
                Assert.Equal(firstResult.ConditionStatus, result.ConditionStatus);
                Assert.Equal(firstResult.ProgressPercentage, result.ProgressPercentage);
                Assert.Equal(firstResult.PrimaryNextAction?.ActionId, result.PrimaryNextAction?.ActionId);
                Assert.Equal(firstResult.PendingActions.Count, result.PendingActions.Count);
            }
        }

        // Assert: Database state must be completely untouched (zero writes, zero mutations)
        var finalActionCount = await db.ClientActions.CountAsync();
        Assert.Equal(initialActionCount, finalActionCount);

        var finalAction = await db.ClientActions.FirstAsync(a => a.ActionId == action1.ActionId);
        Assert.Equal(ClientActionStatus.Pending, finalAction.Status);
        Assert.Null(finalAction.CompletedAt);
        Assert.Null(finalAction.CompletedByActor);

        var finalEngagement = await db.Engagements.AsNoTracking().FirstAsync(e => e.EngagementId == engagementId);
        Assert.Equal(initialEngagement.Status, finalEngagement.Status);
        Assert.Equal(initialEngagement.Stage, finalEngagement.Stage);
        Assert.Equal(initialEngagement.ClosedAt, finalEngagement.ClosedAt);
    }

    [Fact]
    public async Task GetDashboard_PopulatesNextActionResultProperty()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        db.ClientActions.Add(new ClientAction
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "KYC Upload",
            Status = ClientActionStatus.Pending,
            StageNumber = 1,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        // Act
        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        // Assert
        Assert.NotNull(dashboard);
        Assert.NotNull(dashboard.NextAction);
        Assert.Equal(engagementId, dashboard.NextAction.EngagementId);
        Assert.Equal(OverallState.ClientActionRequired, dashboard.NextAction.OverallState);
        Assert.NotNull(dashboard.NextAction.PrimaryAction);
        Assert.Equal("KYC Upload", dashboard.NextAction.PrimaryAction.Title);
    }

    [Fact]
    public async Task GetDashboard_RepeatedReads_ReturnIdenticalActionIds()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        // A requirement with no mirrored action yields an engine item without an ActionId.
        db.Requirements.Add(new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "TaxId",
            Status = RequirementStatus.Requested,
            StageNumber = 1,
            CreatedAt = DateTime.UtcNow
        });
        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload passport",
            Type = ClientActionType.KycDocument,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 1
        });
        await db.SaveChangesAsync();

        var service = CreatePortalService(db);

        var first = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);
        var second = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        Assert.NotNull(first?.PrimaryNextAction);
        Assert.Equal(first!.PrimaryNextAction!.ActionId, second!.PrimaryNextAction!.ActionId);
        Assert.Equal(
            first.PendingActions.Select(a => a.ActionId),
            second.PendingActions.Select(a => a.ActionId));
    }

    [Fact]
    public async Task GetDashboard_ConditionWithLinkedAction_AppearsOnceWithPersistedActionId()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";
        var conditionId = Guid.NewGuid();
        var linkedActionId = Guid.NewGuid();

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Verification
        });
        db.ClientActions.Add(new ClientAction
        {
            ActionId = linkedActionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Scope approval",
            Type = ClientActionType.Approval,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 3,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId
        });
        await db.SaveChangesAsync();

        var condition = new EngagementCondition
        {
            ConditionId = conditionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = ConditionType.Approval,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Scope approval",
            InternalNote = "Staff only: client disputed the fee"
        };

        var service = CreatePortalService(db, activeConditions: new[] { condition });

        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        Assert.NotNull(dashboard?.PrimaryNextAction);
        Assert.Equal(linkedActionId, dashboard!.PrimaryNextAction!.ActionId);
        Assert.DoesNotContain(dashboard.PendingActions, a => a.ActionId == linkedActionId);
        Assert.Equal(1, new[] { dashboard.PrimaryNextAction }.Concat(dashboard.PendingActions).Count(a => a.Title == "Scope approval"));
    }

    [Fact]
    public async Task GetDashboard_SatisfiedConditionWithPendingLinkedAction_IsNotShownAsPending()
    {
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-test";
        var clientId = "client-alpha";
        var conditionId = Guid.NewGuid();

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Verification
        });
        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Scope approval",
            Type = ClientActionType.Approval,
            Status = ClientActionStatus.Pending,
            AssignedToRole = "Client",
            StageNumber = 3,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId
        });
        await db.SaveChangesAsync();

        var satisfied = new EngagementCondition
        {
            ConditionId = conditionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = ConditionType.Approval,
            Status = ConditionStatus.Satisfied,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution,
            Title = "Scope approval"
        };

        var service = CreatePortalService(db, activeConditions: new[] { satisfied });

        var dashboard = await service.GetDashboardForEngagementAsync(engagementId, tenantId, clientId);

        Assert.NotNull(dashboard);
        Assert.Null(dashboard!.PrimaryNextAction);
        Assert.DoesNotContain(dashboard.PendingActions, a => a.Title == "Scope approval");
    }
}
