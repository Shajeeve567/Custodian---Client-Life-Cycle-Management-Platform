using Custodian.Shared.Contracts;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientActionServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    /// <summary>
    /// Default test double: gate always satisfied, so existing tests that don't care about
    /// CSTD-18 gating are unaffected. Tests that specifically exercise the gate-check inject
    /// their own Mock&lt;IGateEvaluator&gt; instead of calling this helper.
    /// </summary>
    private static ClientActionService CreateService(WorkflowDbContext db)
    {
        var gateEvaluator = new Mock<IGateEvaluator>();
        gateEvaluator
            .Setup(g => g.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        return new ClientActionService(db, gateEvaluator.Object, new Mock<IAuditPublisher>().Object);
    }

    [Fact]
    public async Task GetActions_StaffCaller_ReturnsAllActionsIncludingInternalAndMetadata()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.AddRange(
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Public Client Action",
                Type = "DocumentUpload",
                Status = "Pending",
                Source = "Step1",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CompletedByActor = "staff-reviewer-1",
                SourceMetadata = "{\"internalNote\":\"public step\"}"
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Internal Staff Audit Check",
                Type = "VerificationCheck",
                Status = "Pending",
                Source = "SystemGate",
                IsInternalOnly = true,
                AssignedToRole = "Staff",
                SourceMetadata = "{\"internalNote\":\"staff eyes only\"}"
            }
        );
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: Call service with isClientView = false (Staff View)
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false);

        // Assert: Staff receives both items, including internal action, source metadata,
        // and operational fields (CSTD-12 fix must preserve the existing staff response).
        var list = result.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.IsInternalOnly);
        Assert.All(list, a => Assert.NotNull(a.SourceMetadata));
        Assert.All(list, a => Assert.NotNull(a.AssignedToRole));
        Assert.Contains(list, a => a.CompletedByActor == "staff-reviewer-1");
    }

    [Fact]
    public async Task GetActions_ClientCaller_StripsInternalActionsAndSanitizesSourceMetadata()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.AddRange(
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Upload ID Proof",
                Type = "DocumentUpload",
                Status = "Pending",
                Source = "Step1",
                IsInternalOnly = false,
                AssignedToRole = "Client",
                CompletedByActor = "staff-reviewer-7",
                SourceMetadata = "{\"sensitiveData\":\"secret\"}"
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Staff Background Verification",
                Type = "VerificationCheck",
                Status = "Pending",
                Source = "SystemGate",
                IsInternalOnly = true,
                AssignedToRole = "Staff",
                SourceMetadata = "{\"internalNote\":\"secret\"}"
            }
        );
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: Call service with isClientView = true (Client View)
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: true);

        // Assert: Client view strips internal actions, clears SourceMetadata, and (CSTD-12 fix)
        // also strips internal operational metadata (AssignedToRole, CompletedByActor) even
        // from the one action a client is legitimately allowed to see.
        var list = result.ToList();
        Assert.Single(list);
        Assert.Equal("Upload ID Proof", list[0].Title);
        Assert.False(list[0].IsInternalOnly);
        Assert.Null(list[0].SourceMetadata);
        Assert.Null(list[0].AssignedToRole);
        Assert.Null(list[0].CompletedByActor);
    }

    [Fact]
    public async Task GetActions_CrossTenant_ReturnsEmptyList()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = "tenant-legitimate",
            Title = "Task",
            Status = "Pending",
            Source = "Step1"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: Attempt to retrieve action with wrong tenant ID
        var result = await service.GetActionsByEngagementAsync(engagementId, "tenant-attacker", isClientView: false);

        // Assert: Expect empty list due to tenant isolation
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActions_StatusFilter_ReturnsFilteredActions()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.AddRange(
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Task 1",
                Status = "Pending",
                Source = "Step1"
            },
            new ClientAction
            {
                ActionId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Title = "Task 2",
                Status = "Completed",
                Source = "Step1"
            }
        );
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: Filter by status = "Completed"
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false, statusFilter: "Completed");

        // Assert
        var list = result.ToList();
        Assert.Single(list);
        Assert.Equal("Completed", list[0].Status);
    }

    [Fact]
    public async Task CreateActionAsync_ValidRequest_PersistsAndReturnsDto()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var service = CreateService(db);
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        var createDto = new CreateClientActionDto
        {
            Title = "Verify Address",
            Description = "Upload utility bill",
            Type = "DocumentUpload",
            Source = "OnboardingStep2",
            IsInternalOnly = false,
            AssignedToRole = "Client",
            SourceMetadata = "{\"stepId\":\"2\"}"
        };

        // Act
        var created = await service.CreateActionAsync(engagementId, tenantId, createDto);

        // Assert
        Assert.NotNull(created);
        Assert.Equal("Verify Address", created.Title);
        Assert.Equal("Pending", created.Status);
        Assert.Equal(engagementId, created.EngagementId);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == created.ActionId);
        Assert.NotNull(dbAction);
    }

    [Fact]
    public async Task CompleteActionAsync_ExistingAction_UpdatesStatusAndActor()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Sign Document",
            Status = "Pending",
            Source = "Step1"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        var completeDto = new CompleteClientActionDto
        {
            CompletedByActor = "staff-john-doe"
        };

        // Act
        var result = await service.CompleteActionAsync(engagementId, actionId, tenantId, completeDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Completed", result.Status);
        Assert.Equal("staff-john-doe", result.CompletedByActor);
        Assert.NotNull(result.CompletedAt);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.Equal("Completed", dbAction!.Status);
    }

    [Fact]
    public async Task CompleteActionAsync_RequirementBackedAction_ThrowsArgumentException_AndLeavesActionUnchanged()
    {
        // Arrange: CSTD-16 — a Requirement-backed action must go through
        // PUT /requirements/{id}/submit, not the generic complete endpoint, or the underlying
        // Requirement.Value/Status would never actually get set.
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var requirementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Provide: SourceOfFunds",
            Type = "Requirement",
            Status = ClientActionStatus.Pending,
            Source = "RequirementSync",
            LinkedRequirementId = requirementId
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var completeDto = new CompleteClientActionDto { CompletedByActor = "client-user-1" };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteActionAsync(engagementId, actionId, tenantId, completeDto));

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.Equal(ClientActionStatus.Pending, dbAction!.Status);
        Assert.Null(dbAction.CompletedAt);
    }

    [Fact]
    public async Task CompleteActionAsync_LastActionInStage_GateBlocked_CompletesActionButDoesNotAdvanceStageOrPublishAudit()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-1",
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        // The only non-internal Stage 1 action — completing it is otherwise eligible to
        // auto-advance the engagement into Stage 2 (DocumentCollection).
        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Stage 1 Intake",
            Status = ClientActionStatus.Pending,
            StageNumber = 1,
            IsInternalOnly = false
        });
        await db.SaveChangesAsync();

        var gateEvaluator = new Mock<IGateEvaluator>();
        gateEvaluator
            .Setup(g => g.EvaluateAsync(engagementId, tenantId, EngagementStage.DocumentCollection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Blocked("Required document not yet verified.", Array.Empty<GateRequirementResult>()));
        var auditPublisher = new Mock<IAuditPublisher>();

        var service = new ClientActionService(db, gateEvaluator.Object, auditPublisher.Object);
        var completeDto = new CompleteClientActionDto { CompletedByActor = "client-user-1" };

        // Act
        var result = await service.CompleteActionAsync(engagementId, actionId, tenantId, completeDto);

        // Assert: the action itself still completes — only the engagement's stage advance is gated
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Completed, result.Status);

        var dbEngagement = await db.Engagements.FirstAsync(e => e.EngagementId == engagementId);
        Assert.Equal(EngagementStage.Onboarding, dbEngagement.Stage);

        auditPublisher.Verify(
            a => a.PublishEventAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), "StageChange", It.IsAny<object>()),
            Times.Never());
    }

    [Fact]
    public async Task CompleteActionAsync_LastActionInStage_GateSatisfied_AdvancesStageAndPublishesAuditEvent()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-1",
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Stage 1 Intake",
            Status = ClientActionStatus.Pending,
            StageNumber = 1,
            IsInternalOnly = false
        });
        await db.SaveChangesAsync();

        var gateEvaluator = new Mock<IGateEvaluator>();
        gateEvaluator
            .Setup(g => g.EvaluateAsync(engagementId, tenantId, EngagementStage.DocumentCollection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());
        var auditPublisher = new Mock<IAuditPublisher>();

        var service = new ClientActionService(db, gateEvaluator.Object, auditPublisher.Object);
        var completeDto = new CompleteClientActionDto { CompletedByActor = "client-user-1" };

        // Act
        var result = await service.CompleteActionAsync(engagementId, actionId, tenantId, completeDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Completed, result.Status);

        var dbEngagement = await db.Engagements.FirstAsync(e => e.EngagementId == engagementId);
        Assert.Equal(EngagementStage.DocumentCollection, dbEngagement.Stage);

        auditPublisher.Verify(
            a => a.PublishEventAsync(engagementId, tenantId, "client-user-1", "StageChange", It.IsAny<object>()),
            Times.Once());
    }

    [Fact]
    public async Task CreateActionAsync_WithStageAndDeadline_PersistsAndMapsFieldsCorrectly()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var deadline = DateTime.UtcNow.AddDays(3);

        var service = CreateService(db);
        var createDto = new CreateClientActionDto
        {
            Title = "Submit Proof of Address",
            Type = "DocumentUpload",
            StageNumber = 2,
            DeadlineUtc = deadline,
            Source = "Stage2Compliance",
            IsInternalOnly = false,
            AssignedToRole = "Client"
        };

        // Act
        var created = await service.CreateActionAsync(engagementId, tenantId, createDto);

        // Assert
        Assert.NotNull(created);
        Assert.Equal(2, created.StageNumber);
        Assert.Equal(deadline, created.DeadlineUtc);
        Assert.Equal(ClientActionStatus.Pending, created.Status);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == created.ActionId);
        Assert.NotNull(dbAction);
        Assert.Equal(2, dbAction.StageNumber);
        Assert.Equal(deadline, dbAction.DeadlineUtc);
    }

    [Fact]
    public async Task UploadEvidenceAsync_PendingAction_TransitionsToUploaded()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Tax Certificate",
            Status = ClientActionStatus.Pending
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var uploadDto = new UploadActionEvidenceDto
        {
            UploaderActor = "client-user-1",
            DocumentId = Guid.NewGuid()
        };

        // Act
        var result = await service.UploadEvidenceAsync(engagementId, actionId, tenantId, uploadDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Uploaded, result.Status);
        Assert.Equal("client-user-1", result.CompletedByActor);
        Assert.Null(result.CompletedAt); // Still awaiting review

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.Equal(ClientActionStatus.Uploaded, dbAction!.Status);
    }

    [Fact]
    public async Task UploadEvidenceAsync_RejectedCompliance_TransitionsToRejected_PreservingDeterministicReason()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var documentId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Utility Bill",
            Status = ClientActionStatus.Pending
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var uploadDto = new UploadActionEvidenceDto
        {
            UploaderActor = "client-user-1",
            DocumentId = documentId,
            ComplianceStatus = "Rejected",
            RejectionReason = "Document exceeds maximum allowable age of 90 days (issued 120 days ago)."
        };

        // Act
        var result = await service.UploadEvidenceAsync(engagementId, actionId, tenantId, uploadDto);

        // Assert: Workflow contract requires automatic rejection without human staff review
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Rejected, result.Status);
        Assert.Equal("client-user-1", result.CompletedByActor);
        Assert.Null(result.CompletedAt);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(ClientActionStatus.Rejected, dbAction.Status);
        Assert.Contains("Rejected", dbAction.SourceMetadata);
        Assert.Contains("exceeds maximum allowable age of 90 days", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task UploadEvidenceAsync_CompliantCompliance_TransitionsToUploaded_AwaitingHumanVerification()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var documentId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Valid Passport",
            Status = ClientActionStatus.Pending
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var uploadDto = new UploadActionEvidenceDto
        {
            UploaderActor = "client-user-1",
            DocumentId = documentId,
            ComplianceStatus = "Compliant"
        };

        // Act
        var result = await service.UploadEvidenceAsync(engagementId, actionId, tenantId, uploadDto);

        // Assert: Automatic check passed -> transitions to Uploaded (human verification is a separate state)
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Uploaded, result.Status);
        Assert.Equal("client-user-1", result.CompletedByActor);
        Assert.Null(result.CompletedAt);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(ClientActionStatus.Uploaded, dbAction.Status);
        Assert.Contains("Compliant", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task ReviewActionAsync_Accept_TransitionsToCompleted()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Tax Certificate",
            Status = ClientActionStatus.Uploaded
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var reviewDto = new ReviewActionDto
        {
            Status = ClientActionStatus.Completed,
            ReviewerActor = "staff-reviewer-99",
            ReviewNote = "Document verified successfully."
        };

        // Act
        var result = await service.ReviewActionAsync(engagementId, actionId, tenantId, reviewDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Equal("staff-reviewer-99", result.CompletedByActor);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.Equal(ClientActionStatus.Completed, dbAction!.Status);
    }

    [Fact]
    public async Task ReviewActionAsync_Reject_TransitionsToRejected()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Tax Certificate",
            Status = ClientActionStatus.Uploaded
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var reviewDto = new ReviewActionDto
        {
            Status = ClientActionStatus.Rejected,
            ReviewerActor = "staff-reviewer-99",
            ReviewNote = "Image is blurry. Please re-upload a clear copy."
        };

        // Act
        var result = await service.ReviewActionAsync(engagementId, actionId, tenantId, reviewDto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Rejected, result.Status);
        Assert.Null(result.CompletedAt); // Not completed

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.Equal(ClientActionStatus.Rejected, dbAction!.Status);
        Assert.Contains("blurry", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task UploadEvidenceAsync_Compliant_WithVerifiedStatus_TransitionsActionToCompleted()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var documentId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Articles of Incorporation",
            Status = ClientActionStatus.Pending
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var uploadDto = new UploadActionEvidenceDto
        {
            UploaderActor = "client-user-1",
            DocumentId = documentId,
            ComplianceStatus = "Compliant",
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "staff-analyst-1",
            VerificationReason = "State registry seal confirmed valid."
        };

        // Act
        var result = await service.UploadEvidenceAsync(engagementId, actionId, tenantId, uploadDto);

        // Assert: Only human-confirmed evidence satisfies a required gate -> Completed
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Equal("staff-analyst-1", result.CompletedByActor);
        Assert.Equal(DocumentVerificationStatus.Verified, result.VerificationStatus);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(ClientActionStatus.Completed, dbAction.Status);
        Assert.Contains("Compliant", dbAction.SourceMetadata);
        Assert.Contains("Verified", dbAction.SourceMetadata);
        Assert.Contains("State registry seal confirmed valid", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task UploadEvidenceAsync_Compliant_WithRejectedVerification_TransitionsActionToRejected_PreservingCompliance()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var documentId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Articles of Incorporation",
            Status = ClientActionStatus.Pending
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var uploadDto = new UploadActionEvidenceDto
        {
            UploaderActor = "client-user-1",
            DocumentId = documentId,
            ComplianceStatus = "Compliant",
            VerificationStatus = DocumentVerificationStatus.Rejected,
            VerifiedBy = "staff-analyst-1",
            VerificationReason = "Notary signature does not match official registry."
        };

        // Act
        var result = await service.UploadEvidenceAsync(engagementId, actionId, tenantId, uploadDto);

        // Assert: Compliance is preserved as Compliant, but human verification failed -> Rejected
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Rejected, result.Status);
        Assert.Null(result.CompletedAt);
        Assert.Equal("staff-analyst-1", result.CompletedByActor);
        Assert.Equal(DocumentVerificationStatus.Rejected, result.VerificationStatus);
        Assert.Equal("Notary signature does not match official registry.", result.VerificationReason);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(ClientActionStatus.Rejected, dbAction.Status);
        Assert.Contains("Compliant", dbAction.SourceMetadata);
        Assert.Contains("Rejected", dbAction.SourceMetadata);
        Assert.Contains("Notary signature does not match official registry", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task ApplyVerificationOutcomeAsync_Verified_TransitionsToCompleted_AndPreservesMetadata()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var documentId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Passport Verification",
            Status = ClientActionStatus.Uploaded,
            SourceMetadata = $"{{\"documentId\":\"{documentId}\",\"complianceStatus\":\"Compliant\"}}"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Verified,
            VerifiedBy = "compliance-officer-5",
            VerificationReason = "MRZ checksum and human photo verified against passport."
        };

        // Act
        var result = await service.ApplyVerificationOutcomeAsync(engagementId, actionId, tenantId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Completed, result.Status);
        Assert.NotNull(result.CompletedAt);
        Assert.Equal("compliance-officer-5", result.CompletedByActor);
        Assert.Equal(DocumentVerificationStatus.Verified, result.VerificationStatus);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(ClientActionStatus.Completed, dbAction.Status);
        Assert.Contains(documentId.ToString(), dbAction.SourceMetadata);
        Assert.Contains("Compliant", dbAction.SourceMetadata);
        Assert.Contains("Verified", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task ApplyVerificationOutcomeAsync_Rejected_TransitionsToRejected_WithStaffReason()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var documentId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Passport Verification",
            Status = ClientActionStatus.Uploaded,
            SourceMetadata = $"{{\"documentId\":\"{documentId}\",\"complianceStatus\":\"Compliant\"}}"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = DocumentVerificationStatus.Rejected,
            VerifiedBy = "compliance-officer-5",
            VerificationReason = "Passport page is cropped; MRZ lines are cut off."
        };

        // Act
        var result = await service.ApplyVerificationOutcomeAsync(engagementId, actionId, tenantId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Rejected, result.Status);
        Assert.Null(result.CompletedAt);
        Assert.Equal("compliance-officer-5", result.CompletedByActor);
        Assert.Equal(DocumentVerificationStatus.Rejected, result.VerificationStatus);
        Assert.Equal("Passport page is cropped; MRZ lines are cut off.", result.VerificationReason);

        var dbAction = await db.ClientActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(ClientActionStatus.Rejected, dbAction.Status);
        Assert.Contains(documentId.ToString(), dbAction.SourceMetadata);
        Assert.Contains("Compliant", dbAction.SourceMetadata);
        Assert.Contains("Rejected", dbAction.SourceMetadata);
        Assert.Contains("Passport page is cropped", dbAction.SourceMetadata);
    }

    [Fact]
    public async Task ApplyVerificationOutcomeAsync_InvalidStatus_ThrowsArgumentException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Passport Verification",
            Status = ClientActionStatus.Uploaded
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = "InvalidStatus",
            VerifiedBy = "compliance-officer-5"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyVerificationOutcomeAsync(engagementId, actionId, tenantId, dto));
    }

    [Fact]
    public async Task EnsureLifecycleActionsAsync_SeedsAllFiveStages()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-seeder";

        var service = CreateService(db);

        // Act
        var actions = await service.EnsureLifecycleActionsAsync(engagementId, tenantId);

        // Assert
        Assert.NotNull(actions);
        Assert.True(actions.Count >= 5);
        Assert.Contains(actions, a => a.StageNumber == 1);
        Assert.Contains(actions, a => a.StageNumber == 2 && a.Type == "KycDocument");
        Assert.Contains(actions, a => a.StageNumber == 3);
        Assert.Contains(actions, a => a.StageNumber == 4);
        Assert.Contains(actions, a => a.StageNumber == 5);
    }

    [Fact]
    public void GenerateDefaultLifecycleActions_Stage2_IncludesProofOfAddress()
    {
        // Arrange / Act: GateRequirements requires BOTH KYC_PASSPORT and PROOF_OF_ADDRESS to
        // leave Document Collection — the seeded task list must actually ask the client for
        // both, or the gate becomes satisfiable only via an unguided vault upload.
        var actions = ClientActionService.GenerateDefaultLifecycleActions(Guid.NewGuid(), "tenant-seeder");

        // Assert
        Assert.Contains(actions, a => a.StageNumber == 2 && a.Type == "KycDocument");
        Assert.Contains(actions, a => a.StageNumber == 2 && a.Type == "ProofOfAddress");
        Assert.Contains(actions, a => a.StageNumber == 2 && a.Type == "SignAgreement");
    }

    [Fact]
    public async Task GetActionsByEngagementAsync_WhenEngagementExistsAndHasNoActions_AutoSeedsLifecycleActions()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-seeder";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-1",
            StaffId = "staff-1",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act
        var actions = (await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false)).ToList();

        // Assert
        Assert.NotEmpty(actions);
        Assert.Contains(actions, a => a.StageNumber == 1);
        Assert.Contains(actions, a => a.StageNumber == 2);
    }

    [Fact]
    public async Task ClientOwnsEngagementAsync_MatchingClientAndTenant_ReturnsTrue()
    {
        // Arrange (CSTD-22 IDOR protection, extended to ClientActionsController)
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-owner",
            StaffId = "staff-1"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act
        var owns = await service.ClientOwnsEngagementAsync(engagementId, tenantId, "client-owner");

        // Assert
        Assert.True(owns);
    }

    [Fact]
    public async Task ClientOwnsEngagementAsync_DifferentClient_ReturnsFalse()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-owner",
            StaffId = "staff-1"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: a different client in the same tenant
        var owns = await service.ClientOwnsEngagementAsync(engagementId, tenantId, "client-attacker");

        // Assert
        Assert.False(owns);
    }

    [Fact]
    public async Task ClientOwnsEngagementAsync_DifferentTenant_ReturnsFalse()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = "tenant-legitimate",
            ClientId = "client-owner",
            StaffId = "staff-1"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act
        var owns = await service.ClientOwnsEngagementAsync(engagementId, "tenant-attacker", "client-owner");

        // Assert
        Assert.False(owns);
    }

    [Fact]
    public async Task GetActions_StaffCaller_ExposesNewLifecycleAndSourceLinks()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var activatedAt = DateTime.UtcNow.AddHours(-2);
        var updatedAt = DateTime.UtcNow.AddMinutes(-30);
        var docId = Guid.NewGuid();
        var condId = Guid.NewGuid();
        var meetId = Guid.NewGuid();
        var reqId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Staff Full View Action",
            Type = ClientActionType.DocumentUpload,
            Status = ClientActionStatus.Pending,
            Source = "ManualTest",
            SourceType = ClientActionSourceType.Document,
            ActivatedAt = activatedAt,
            UpdatedAt = updatedAt,
            LinkedRequirementId = reqId,
            LinkedDocumentId = docId,
            LinkedConditionId = condId,
            LinkedMeetingId = meetId,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: staff view (isClientView = false)
        var result = (await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false)).ToList();

        // Assert: all fields are exposed in staff view
        Assert.Single(result);
        var dto = result[0];
        Assert.Equal(activatedAt, dto.ActivatedAt);
        Assert.Equal(ClientActionSourceType.Document, dto.SourceType);
        Assert.Equal(updatedAt, dto.UpdatedAt);
        Assert.Equal(reqId, dto.LinkedRequirementId);
        Assert.Equal(docId, dto.LinkedDocumentId);
        Assert.Equal(condId, dto.LinkedConditionId);
        Assert.Equal(meetId, dto.LinkedMeetingId);
    }

    [Fact]
    public async Task GetActions_ClientCaller_StripsSensitiveSourceLinksAndActivatedAt()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var activatedAt = DateTime.UtcNow.AddHours(-2);
        var updatedAt = DateTime.UtcNow.AddMinutes(-30);
        var docId = Guid.NewGuid();
        var condId = Guid.NewGuid();
        var meetId = Guid.NewGuid();
        var reqId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Client Safe View Action",
            Type = ClientActionType.DocumentUpload,
            Status = ClientActionStatus.Pending,
            Source = "ManualTest",
            SourceType = ClientActionSourceType.Document,
            ActivatedAt = activatedAt,
            UpdatedAt = updatedAt,
            LinkedRequirementId = reqId,
            LinkedDocumentId = docId,
            LinkedConditionId = condId,
            LinkedMeetingId = meetId,
            IsInternalOnly = false,
            AssignedToRole = "Client"
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: client view (isClientView = true)
        var result = (await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: true)).ToList();

        // Assert: client view preserves SourceType and LinkedRequirementId, but strips ActivatedAt and external linked IDs
        Assert.Single(result);
        var dto = result[0];
        Assert.Null(dto.ActivatedAt);
        Assert.Null(dto.LinkedDocumentId);
        Assert.Null(dto.LinkedConditionId);
        Assert.Null(dto.LinkedMeetingId);
        Assert.Equal(ClientActionSourceType.Document, dto.SourceType);
        Assert.Equal(reqId, dto.LinkedRequirementId);
        Assert.Equal(updatedAt, dto.UpdatedAt);
    }

    [Fact]
    public void ClientActionSchema_ConstantsAndDefaults_AreProperlyConfigured()
    {
        // Assert: ClientActionStatus includes Cancelled
        Assert.Equal("Cancelled", ClientActionStatus.Cancelled);

        // Assert: ClientActionSourceType constants
        Assert.Equal("Requirement", ClientActionSourceType.Requirement);
        Assert.Equal("Document", ClientActionSourceType.Document);
        Assert.Equal("Condition", ClientActionSourceType.Condition);
        Assert.Equal("Meeting", ClientActionSourceType.Meeting);
        Assert.Equal("Lifecycle", ClientActionSourceType.Lifecycle);
        Assert.Equal("Manual", ClientActionSourceType.Manual);

        // Assert: ClientActionType constants
        Assert.Equal("DocumentUpload", ClientActionType.DocumentUpload);
        Assert.Equal("KycDocument", ClientActionType.KycDocument);
        Assert.Equal("SignAgreement", ClientActionType.SignAgreement);
        Assert.Equal("ProofOfAddress", ClientActionType.ProofOfAddress);
        Assert.Equal("CustomTask", ClientActionType.CustomTask);
        Assert.Equal("Requirement", ClientActionType.Requirement);
        Assert.Equal("Approval", ClientActionType.Approval);
        Assert.Equal("Payment", ClientActionType.Payment);
        Assert.Equal("Meeting", ClientActionType.Meeting);

        // Assert: ClientAction defaults
        var action = new ClientAction();
        Assert.Equal(ClientActionStatus.Pending, action.Status);
        Assert.Equal(ClientActionSourceType.Manual, action.SourceType);
        Assert.Equal(ClientActionType.DocumentUpload, action.Type);
        Assert.Null(action.ActivatedAt);
        Assert.Null(action.LinkedDocumentId);
        Assert.Null(action.LinkedConditionId);
        Assert.Null(action.LinkedMeetingId);
    }

    [Fact]
    public async Task ActivateStageActionsAsync_SetsActivatedAt_OnlyForTargetStageAndNullActivatedAt()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var existingActivatedTime = DateTime.UtcNow.AddDays(-1);

        var actionStage1 = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            StageNumber = 1,
            ActivatedAt = null,
            Status = ClientActionStatus.Pending
        };
        var actionStage2Unactivated = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            StageNumber = 2,
            ActivatedAt = null,
            Status = ClientActionStatus.Pending
        };
        var actionStage2AlreadyActivated = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            StageNumber = 2,
            ActivatedAt = existingActivatedTime,
            Status = ClientActionStatus.Pending
        };

        db.ClientActions.AddRange(actionStage1, actionStage2Unactivated, actionStage2AlreadyActivated);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act
        await service.ActivateStageActionsAsync(engagementId, tenantId, 2);

        // Assert
        var dbActions = await db.ClientActions.ToListAsync();
        var updatedStage1 = dbActions.First(a => a.ActionId == actionStage1.ActionId);
        var updatedStage2Unactivated = dbActions.First(a => a.ActionId == actionStage2Unactivated.ActionId);
        var updatedStage2AlreadyActivated = dbActions.First(a => a.ActionId == actionStage2AlreadyActivated.ActionId);

        Assert.Null(updatedStage1.ActivatedAt);
        Assert.NotNull(updatedStage2Unactivated.ActivatedAt);
        Assert.Equal(existingActivatedTime, updatedStage2AlreadyActivated.ActivatedAt);
    }

    [Theory]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public async Task MutatingActions_OnClosedOrCancelledEngagement_ThrowsInvalidOperationException(EngagementStatus terminalStatus)
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            Status = terminalStatus
        });

        var actionId = Guid.NewGuid();
        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Status = ClientActionStatus.Pending,
            Type = ClientActionType.DocumentUpload
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act & Assert: CreateAction throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateActionAsync(engagementId, tenantId, new CreateClientActionDto
            {
                Title = "New Action",
                Type = ClientActionType.CustomTask
            }));

        // Act & Assert: CompleteAction throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteActionAsync(engagementId, actionId, tenantId, new CompleteClientActionDto
            {
                CompletedByActor = "Staff"
            }));

        // Act & Assert: UploadEvidence throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UploadEvidenceAsync(engagementId, actionId, tenantId, new UploadActionEvidenceDto
            {
                DocumentId = Guid.NewGuid(),
                ComplianceStatus = "Compliant",
                UploaderActor = "client-1"
            }));

        // Act & Assert: ReviewAction throws
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReviewActionAsync(engagementId, actionId, tenantId, new ReviewActionDto
            {
                Status = ClientActionStatus.Completed,
                ReviewerActor = "Staff"
            }));
    }

    [Fact]
    public async Task ApplyStatusAsync_TerminalCancelledState_ThrowsInvalidOperationException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            Status = EngagementStatus.Started
        });

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Status = ClientActionStatus.Cancelled,
            Type = ClientActionType.DocumentUpload
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act & Assert: Cannot complete a cancelled action
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteActionAsync(engagementId, actionId, tenantId, new CompleteClientActionDto
            {
                CompletedByActor = "Staff"
            }));

        Assert.Contains("Cancelled", ex.Message);
    }

    [Fact]
    public async Task StatusChange_EmitsClientActionStatusChangedAuditEvent()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            Status = EngagementStatus.Started
        });

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Status = ClientActionStatus.Pending,
            Type = ClientActionType.DocumentUpload,
            SourceType = ClientActionSourceType.Document,
            LinkedDocumentId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();

        var mockAudit = new Mock<IAuditPublisher>();
        var gateEvaluator = new Mock<IGateEvaluator>();
        gateEvaluator
            .Setup(g => g.EvaluateAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        var service = new ClientActionService(db, gateEvaluator.Object, mockAudit.Object);

        // Act
        var result = await service.CompleteActionAsync(engagementId, actionId, tenantId, new CompleteClientActionDto
        {
            CompletedByActor = "StaffMember"
        });

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ClientActionStatus.Completed, result.Status);

        mockAudit.Verify(a => a.PublishEventAsync(
            engagementId,
            tenantId,
            "StaffMember",
            "ClientActionStatusChanged",
            It.Is<object>(payload => payload != null)),
            Times.Once);
    }

    [Theory]
    [InlineData(ClientActionSourceType.Document, true, false, false)]
    [InlineData(ClientActionSourceType.Condition, false, true, false)]
    [InlineData(ClientActionSourceType.Meeting, false, false, true)]
    [InlineData(ClientActionSourceType.Manual, false, false, false)]
    [InlineData(ClientActionSourceType.Lifecycle, false, false, false)]
    public async Task CreateActionAsync_ValidSourceTypesAndLinkedIds_RoundTripsCorrectly(
        string sourceType, bool hasDocId, bool hasCondId, bool hasMeetId)
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            Status = EngagementStatus.Started
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var docId = hasDocId ? Guid.NewGuid() : (Guid?)null;
        var condId = hasCondId ? Guid.NewGuid() : (Guid?)null;
        var meetId = hasMeetId ? Guid.NewGuid() : (Guid?)null;

        var dto = new CreateClientActionDto
        {
            Title = $"Action for {sourceType}",
            Source = "Test",
            SourceType = sourceType,
            LinkedDocumentId = docId,
            LinkedConditionId = condId,
            LinkedMeetingId = meetId
        };

        // Act
        var result = await service.CreateActionAsync(engagementId, tenantId, dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(sourceType, result.SourceType);
        Assert.Equal(docId, result.LinkedDocumentId);
        Assert.Equal(condId, result.LinkedConditionId);
        Assert.Equal(meetId, result.LinkedMeetingId);
    }

    [Fact]
    public async Task CreateActionAsync_InvalidSourceType_ThrowsArgumentException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        var service = CreateService(db);
        var dto = new CreateClientActionDto
        {
            Title = "Invalid Source",
            Source = "Test",
            SourceType = "NonExistentSource"
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateActionAsync(engagementId, tenantId, dto));
        Assert.Contains("Invalid source type", ex.Message);
    }

    [Fact]
    public async Task CreateActionAsync_RequirementSourceType_ThrowsArgumentException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        var service = CreateService(db);
        var dto = new CreateClientActionDto
        {
            Title = "Requirement Source",
            Source = "Test",
            SourceType = ClientActionSourceType.Requirement
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateActionAsync(engagementId, tenantId, dto));
        Assert.Contains("Requirement-linked actions cannot be created directly", ex.Message);
    }

    [Fact]
    public async Task CreateActionAsync_MultipleLinkedIds_ThrowsArgumentException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        var service = CreateService(db);
        var dto = new CreateClientActionDto
        {
            Title = "Multiple Linked IDs",
            Source = "Test",
            SourceType = ClientActionSourceType.Document,
            LinkedDocumentId = Guid.NewGuid(),
            LinkedConditionId = Guid.NewGuid()
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateActionAsync(engagementId, tenantId, dto));
        Assert.Contains("At most one linked identifier", ex.Message);
    }

    [Fact]
    public async Task CreateActionAsync_MismatchedLinkedId_ThrowsArgumentException()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";

        var service = CreateService(db);

        // Document with LinkedConditionId
        var dto1 = new CreateClientActionDto
        {
            Title = "Doc with ConditionId",
            Source = "Test",
            SourceType = ClientActionSourceType.Document,
            LinkedConditionId = Guid.NewGuid()
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateActionAsync(engagementId, tenantId, dto1));

        // Manual with LinkedDocumentId
        var dto2 = new CreateClientActionDto
        {
            Title = "Manual with DocId",
            Source = "Test",
            SourceType = ClientActionSourceType.Manual,
            LinkedDocumentId = Guid.NewGuid()
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateActionAsync(engagementId, tenantId, dto2));
    }

    [Fact]
    public async Task UploadEvidenceAsync_SetsLinkedDocumentId_AndUpdatesSourceTypeToDocument()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var docId = Guid.NewGuid();

        db.ClientActions.Add(new ClientAction
        {
            ActionId = actionId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Upload Financials",
            SourceType = ClientActionSourceType.Manual,
            Status = ClientActionStatus.Pending
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act
        var result = await service.UploadEvidenceAsync(engagementId, actionId, tenantId, new UploadActionEvidenceDto
        {
            DocumentId = docId,
            ComplianceStatus = "Compliant",
            UploaderActor = "client-user"
        });

        // Assert
        Assert.NotNull(result);
        Assert.Equal(docId, result.LinkedDocumentId);
        Assert.Equal(ClientActionSourceType.Document, result.SourceType);
        Assert.Contains(docId.ToString(), result.SourceMetadata);

        var dbAction = await db.ClientActions.FindAsync(actionId);
        Assert.NotNull(dbAction);
        Assert.Equal(docId, dbAction.LinkedDocumentId);
        Assert.Equal(ClientActionSourceType.Document, dbAction.SourceType);
    }

    [Fact]
    public async Task CreateLinkedActionAsync_ConditionAndMeeting_CreatesActionWithCorrectLinks()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var conditionId = Guid.NewGuid();
        var meetingId = Guid.NewGuid();

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            Status = EngagementStatus.Started
        });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act 1: Condition action
        var condAction = await service.CreateLinkedActionAsync(
            engagementId,
            tenantId,
            ClientActionSourceType.Condition,
            conditionId,
            "Sign Off Condition 4B",
            description: "Owner approval required",
            type: ClientActionType.Approval);

        // Act 2: Meeting action
        var meetAction = await service.CreateLinkedActionAsync(
            engagementId,
            tenantId,
            new CreateLinkedActionDto
            {
                Title = "Kickoff Strategy Meeting",
                SourceType = ClientActionSourceType.Meeting,
                SourceId = meetingId,
                Type = ClientActionType.Meeting
            });

        // Assert
        Assert.NotNull(condAction);
        Assert.Equal(ClientActionSourceType.Condition, condAction.SourceType);
        Assert.Equal(conditionId, condAction.LinkedConditionId);
        Assert.Equal(ClientActionType.Approval, condAction.Type);

        Assert.NotNull(meetAction);
        Assert.Equal(ClientActionSourceType.Meeting, meetAction.SourceType);
        Assert.Equal(meetingId, meetAction.LinkedMeetingId);
        Assert.Equal(ClientActionType.Meeting, meetAction.Type);
    }

    [Fact]
    public async Task CancelActionsForSourceAsync_CancelsPendingAndUploadedActions_LeavesCompletedUntouched()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var conditionId = Guid.NewGuid();

        db.Engagements.Add(new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            Status = EngagementStatus.Started
        });

        var pendingAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId,
            Status = ClientActionStatus.Pending
        };
        var uploadedAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId,
            Status = ClientActionStatus.Uploaded
        };
        var completedAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = conditionId,
            Status = ClientActionStatus.Completed
        };
        var unrelatedAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            SourceType = ClientActionSourceType.Condition,
            LinkedConditionId = Guid.NewGuid(),
            Status = ClientActionStatus.Pending
        };

        db.ClientActions.AddRange(pendingAction, uploadedAction, completedAction, unrelatedAction);
        await db.SaveChangesAsync();

        var mockAudit = new Mock<IAuditPublisher>();
        var gateEvaluator = new Mock<IGateEvaluator>();
        var service = new ClientActionService(db, gateEvaluator.Object, mockAudit.Object);

        // Act
        await service.CancelActionsForSourceAsync(
            engagementId,
            tenantId,
            ClientActionSourceType.Condition,
            conditionId,
            "StaffUser",
            "Condition waived by credit committee");

        // Assert
        var updatedPending = await db.ClientActions.FindAsync(pendingAction.ActionId);
        var updatedUploaded = await db.ClientActions.FindAsync(uploadedAction.ActionId);
        var updatedCompleted = await db.ClientActions.FindAsync(completedAction.ActionId);
        var updatedUnrelated = await db.ClientActions.FindAsync(unrelatedAction.ActionId);

        Assert.Equal(ClientActionStatus.Cancelled, updatedPending!.Status);
        Assert.Equal(ClientActionStatus.Cancelled, updatedUploaded!.Status);
        Assert.Equal(ClientActionStatus.Completed, updatedCompleted!.Status);
        Assert.Equal(ClientActionStatus.Pending, updatedUnrelated!.Status);

        // Audit emitted for pending and uploaded actions
        mockAudit.Verify(a => a.PublishEventAsync(
            engagementId,
            tenantId,
            "StaffUser",
            "ClientActionStatusChanged",
            It.IsAny<object>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetActionsByEngagementAsync_ClientSafeDto_StrictlyStripsSensitiveFieldsAndExcludesInternalActions()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var reqId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var condId = Guid.NewGuid();
        var meetId = Guid.NewGuid();

        var internalAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Internal Staff Review",
            IsInternalOnly = true,
            Status = ClientActionStatus.Pending
        };

        var clientAction = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Submit Proof of Identity",
            IsInternalOnly = false,
            Status = ClientActionStatus.Pending,
            SourceType = ClientActionSourceType.Document,
            SourceMetadata = "{\"documentId\":\"123\",\"complianceStatus\":\"Compliant\",\"staffNotes\":\"Confidential\"}",
            AssignedToRole = "Client",
            CompletedByActor = "StaffAuditor",
            ActivatedAt = DateTime.UtcNow,
            LinkedRequirementId = reqId,
            LinkedDocumentId = docId,
            LinkedConditionId = condId,
            LinkedMeetingId = meetId
        };

        db.ClientActions.AddRange(internalAction, clientAction);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        // Act: Request client view (isClientView: true)
        var clientViewActions = (await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: true)).ToList();

        // Assert: Internal actions are completely excluded
        Assert.Single(clientViewActions);
        var dto = clientViewActions[0];
        Assert.Equal(clientAction.ActionId, dto.ActionId);

        // Assert: Sensitive staff/internal metadata fields are stripped
        Assert.Null(dto.SourceMetadata);
        Assert.Null(dto.AssignedToRole);
        Assert.Null(dto.CompletedByActor);
        Assert.Null(dto.ActivatedAt);
        Assert.Null(dto.LinkedDocumentId);
        Assert.Null(dto.LinkedConditionId);
        Assert.Null(dto.LinkedMeetingId);

        // Assert: Client-relevant fields are preserved
        Assert.Equal(ClientActionSourceType.Document, dto.SourceType);
        Assert.Equal(reqId, dto.LinkedRequirementId);
        Assert.False(dto.IsInternalOnly);
    }
}
