using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Microsoft.EntityFrameworkCore;
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
                SourceMetadata = "{\"internalNote\":\"staff eyes only\"}"
            }
        );
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        // Act: Call service with isClientView = false (Staff View)
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: false);

        // Assert: Staff receives both items, including internal action and source metadata
        var list = result.ToList();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, a => a.IsInternalOnly);
        Assert.All(list, a => Assert.NotNull(a.SourceMetadata));
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
                SourceMetadata = "{\"internalNote\":\"secret\"}"
            }
        );
        await db.SaveChangesAsync();

        var service = new ClientActionService(db);

        // Act: Call service with isClientView = true (Client View)
        var result = await service.GetActionsByEngagementAsync(engagementId, tenantId, isClientView: true);

        // Assert: Client view strips internal actions and clears SourceMetadata
        var list = result.ToList();
        Assert.Single(list);
        Assert.Equal("Upload ID Proof", list[0].Title);
        Assert.False(list[0].IsInternalOnly);
        Assert.Null(list[0].SourceMetadata);
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

        var service = new ClientActionService(db);

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

        var service = new ClientActionService(db);

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
        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);

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
    public async Task CreateActionAsync_WithStageAndDeadline_PersistsAndMapsFieldsCorrectly()
    {
        // Arrange
        using var db = CreateInMemoryDbContext(Guid.NewGuid().ToString());
        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var deadline = DateTime.UtcNow.AddDays(3);

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
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

        var service = new ClientActionService(db);
        var dto = new ApplyActionVerificationDto
        {
            VerificationStatus = "InvalidStatus",
            VerifiedBy = "compliance-officer-5"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyVerificationOutcomeAsync(engagementId, actionId, tenantId, dto));
    }
}
