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

public class NextActionServiceTests
{
    private static WorkflowDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new WorkflowDbContext(options);
    }

    [Fact]
    public async Task AC7_RequirementSubmit_ChangesPrimaryFromSubmissionToReview()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };
        var requirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = "ArticlesOfIncorporation",
            Status = RequirementStatus.Requested,
            StageNumber = 1,
            CreatedAt = DateTime.UtcNow
        };

        await db.Engagements.AddAsync(engagement);
        await db.Requirements.AddAsync(requirement);
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        mockGateEvaluator.Setup(g => g.EvaluateAsync(engagementId, tenantId, It.IsAny<EngagementStage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        var mockStall = new Mock<IStallService>();
        mockStall.Setup(s => s.GetStallStatusAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var slaCalculator = new DefaultSlaCalculator();
        var service = new NextActionService(
            db,
            mockConditionService.Object,
            mockDocClient.Object,
            slaCalculator,
            mockGateEvaluator.Object,
            mockStall.Object,
            NullLogger<NextActionService>.Instance);

        // 1. Initial read: primary is requirement submission (Client)
        var initialResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(initialResult);
        Assert.Equal(NextActionKind.RequirementSubmission, initialResult.PrimaryAction?.Kind);
        Assert.Equal(ResponsibleParty.Client, initialResult.PrimaryAction?.ResponsibleParty);
        Assert.Equal(3, initialResult.PrimaryAction?.PriorityRank);

        // 2. Mutate dependency: client submits the requirement
        requirement.Status = RequirementStatus.Submitted;
        requirement.Value = "https://documents.example.com/articles.pdf";
        requirement.SubmittedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // 3. Next read reflects change without caching (compute-on-read): primary is now requirement review (Staff)
        var updatedResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(updatedResult);
        Assert.Equal(NextActionKind.RequirementReview, updatedResult.PrimaryAction?.Kind);
        Assert.Equal(ResponsibleParty.Staff, updatedResult.PrimaryAction?.ResponsibleParty);
        Assert.Equal(10, updatedResult.PrimaryAction?.PriorityRank);
    }

    [Fact]
    public async Task AC7_VerifyDocument_ChangesPrimaryToAdvanceStage()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };
        var docId = Guid.NewGuid();
        var action = new ClientAction
        {
            ActionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Title = "Certificate of Incorporation",
            Type = "DocumentUpload",
            Status = ClientActionStatus.Uploaded,
            AssignedToRole = "Staff",
            StageNumber = 1,
            LinkedDocumentId = docId
        };

        await db.Engagements.AddAsync(engagement);
        await db.ClientActions.AddAsync(action);
        await db.SaveChangesAsync();

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        // Document initially unverified
        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DocumentSummaryDto
                {
                    DocumentId = docId,
                    Type = "CertificateOfIncorporation",
                    ComplianceStatus = "Compliant",
                    VerificationStatus = "Unverified"
                }
            });

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        mockGateEvaluator.Setup(g => g.EvaluateAsync(engagementId, tenantId, EngagementStage.DocumentCollection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        var service = new NextActionService(
            db,
            mockConditionService.Object,
            mockDocClient.Object,
            new DefaultSlaCalculator(),
            mockGateEvaluator.Object,
            new DefaultStallService(),
            NullLogger<NextActionService>.Instance);

        // 1. Initial read: primary is document verification (Staff)
        var initialResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(initialResult);
        Assert.Equal(NextActionKind.DocumentVerification, initialResult.PrimaryAction?.Kind);
        Assert.Equal(9, initialResult.PrimaryAction?.PriorityRank);

        // 2. Document is verified and action marked completed
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new DocumentSummaryDto
                {
                    DocumentId = docId,
                    Type = "CertificateOfIncorporation",
                    ComplianceStatus = "Compliant",
                    VerificationStatus = "Verified"
                }
            });
        action.Status = ClientActionStatus.Completed;
        await db.SaveChangesAsync();

        // 3. Next read reflects change: all items satisfied -> AdvanceStage
        var updatedResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(updatedResult);
        Assert.Equal(OverallState.ReadyToAdvance, updatedResult.OverallState);
        Assert.Equal(NextActionKind.AdvanceStage, updatedResult.PrimaryAction?.Kind);
        Assert.Equal(12, updatedResult.PrimaryAction?.PriorityRank);
    }

    [Fact]
    public async Task AC7_DeactivateCondition_BlockerDisappears()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = CreateInMemoryDbContext(dbName);

        var engagementId = Guid.NewGuid();
        var tenantId = "tenant-001";
        var engagement = new Engagement
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Status = EngagementStatus.Started,
            Stage = EngagementStage.Onboarding,
            CreatedAt = DateTime.UtcNow
        };

        await db.Engagements.AddAsync(engagement);
        await db.SaveChangesAsync();

        var activeCondition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = ConditionType.Approval,
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.DocumentCollection,
            Title = "Legal Counsel Approval"
        };

        var mockConditionService = new Mock<IConditionService>();
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { activeCondition });

        var mockDocClient = new Mock<IDocumentComplianceClient>();
        mockDocClient.Setup(d => d.GetDocumentsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DocumentSummaryDto>());

        var mockGateEvaluator = new Mock<IGateEvaluator>();
        mockGateEvaluator.Setup(g => g.EvaluateAsync(engagementId, tenantId, EngagementStage.DocumentCollection, It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateEvaluationResult.Satisfied());

        var service = new NextActionService(
            db,
            mockConditionService.Object,
            mockDocClient.Object,
            new DefaultSlaCalculator(),
            mockGateEvaluator.Object,
            new DefaultStallService(),
            NullLogger<NextActionService>.Instance);

        // 1. Initial read: condition approval is primary
        var initialResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(initialResult);
        Assert.Equal(NextActionKind.ConditionApproval, initialResult.PrimaryAction?.Kind);
        Assert.Equal(5, initialResult.PrimaryAction?.PriorityRank);

        // 2. Condition is deactivated (no active conditions returned)
        mockConditionService.Setup(c => c.GetActiveConditionsAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EngagementCondition>());

        // 3. Next read reflects change: condition blocker disappeared, ready to advance
        var updatedResult = await service.GetNextActionAsync(engagementId, tenantId, NextActionView.Staff);
        Assert.NotNull(updatedResult);
        Assert.Equal(OverallState.ReadyToAdvance, updatedResult.OverallState);
        Assert.Equal(NextActionKind.AdvanceStage, updatedResult.PrimaryAction?.Kind);
    }
}
