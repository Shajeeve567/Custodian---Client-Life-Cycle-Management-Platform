using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for GateEvaluator (CSTD-18 & CSTD-24). Mocks IDocumentComplianceClient
/// and IConditionService so these run without external services.
/// </summary>
public class GateEvaluatorTests
{
    private readonly Mock<IDocumentComplianceClient> _mockDocumentClient;
    private readonly Mock<IConditionService> _mockConditionService;
    private readonly GateEvaluator _evaluator;
    private readonly Guid _engagementId = Guid.NewGuid();
    private const string TenantId = "tenant-001";

    public GateEvaluatorTests()
    {
        _mockDocumentClient = new Mock<IDocumentComplianceClient>();
        _mockConditionService = new Mock<IConditionService>();

        // Default: no active conditions
        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EngagementCondition>());

        _evaluator = new GateEvaluator(
            _mockDocumentClient.Object,
            _mockConditionService.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GateEvaluator>.Instance);
    }

    private static DocumentSummaryDto MakeDoc(string type, string compliance, string verification, bool isDeleted = false) => new()
    {
        DocumentId = Guid.NewGuid(),
        Type = type,
        ComplianceStatus = compliance,
        VerificationStatus = verification,
        IsDeleted = isDeleted
    };

    [Theory]
    [InlineData(EngagementStage.Onboarding)]
    [InlineData(EngagementStage.DocumentCollection)]
    [InlineData(EngagementStage.Closure)]
    public async Task EvaluateAsync_TargetStageHasNoGateRequirements_ReturnsSatisfiedWithoutCallingDocumentsService(EngagementStage targetStage)
    {
        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, targetStage);

        // Assert: no requirements defined for these stages, so the Documents service is never called
        Assert.True(result.IsSatisfied);
        _mockDocumentClient.Verify(c => c.GetDocumentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EvaluateAsync_RequiredDocumentMissing_ReturnsBlockedWithClearReason()
    {
        // Arrange: entering Verification requires KYC_PASSPORT + PROOF_OF_ADDRESS; supply neither
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>());

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.NotNull(result.Reason);
        Assert.Contains("KYC_PASSPORT", result.Reason);
        Assert.Contains("PROOF_OF_ADDRESS", result.Reason);
        Assert.Contains("has not been submitted", result.Reason);
        Assert.Equal(2, result.Requirements.Count);
        Assert.All(result.Requirements, r => Assert.False(r.IsSatisfied));
    }

    [Fact]
    public async Task EvaluateAsync_DocumentExistsButNonCompliant_ReturnsBlockedWithStatusInReason()
    {
        // Arrange: KYC_PASSPORT is NonCompliant; PROOF_OF_ADDRESS is Compliant
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "NonCompliant", "Unverified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Unverified"),
            });

        // Act: entering Verification only requires compliance (not verification)
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("KYC_PASSPORT", result.Reason);
        Assert.Contains("NonCompliant", result.Reason);
        Assert.DoesNotContain("PROOF_OF_ADDRESS", result.Reason);
        Assert.Equal(2, result.Requirements.Count);

        var passport = Assert.Single(result.Requirements, r => r.RequirementName == "KYC_PASSPORT");
        Assert.False(passport.IsSatisfied);

        var poa = Assert.Single(result.Requirements, r => r.RequirementName == "PROOF_OF_ADDRESS");
        Assert.True(poa.IsSatisfied);
    }

    [Fact]
    public async Task EvaluateAsync_AllDocumentsCompliant_EnteringVerification_ReturnsSatisfied()
    {
        // Arrange: both required documents are Compliant (verification not required for Verification stage)
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Unverified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Unverified"),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        // Assert
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
        Assert.Equal(2, result.Requirements.Count);
        Assert.All(result.Requirements, r => Assert.True(r.IsSatisfied));
    }

    [Fact]
    public async Task EvaluateAsync_DocumentCompliantButNotVerified_EnteringExecution_ReturnsBlocked()
    {
        // Arrange: entering Execution requires staff verification — auto-compliant is not enough
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Unverified"), // Compliant but not verified
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.NotNull(result.Reason);
        Assert.Contains("PROOF_OF_ADDRESS", result.Reason);
        Assert.Contains("compliant but not yet verified", result.Reason);
        Assert.DoesNotContain("KYC_PASSPORT", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_AllDocumentsVerified_EnteringExecution_ReturnsSatisfied()
    {
        // Arrange: both required documents are both Compliant and Verified
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified"),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
        Assert.Equal(2, result.Requirements.Count);
        Assert.All(result.Requirements, r => Assert.True(r.IsSatisfied));
    }

    [Fact]
    public async Task EvaluateAsync_MultipleCandidatesOneSatisfies_RequirementIsPassed()
    {
        // Arrange: two passport uploads (e.g. initial re-upload) — one rejected, one verified
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "NonCompliant", "Rejected"),
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified"),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert: the verified candidate satisfies the requirement
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_SoftDeletedDocument_IsIgnoredAndTreatedAsMissing()
    {
        // Arrange: a verified document exists but has been soft-deleted — must not count
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified", isDeleted: true),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified", isDeleted: true),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("has not been submitted", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_DocumentsServiceUnavailable_FailsClosedAndReturnsBlocked()
    {
        // Arrange: the gate must not be silently bypassed if Documents can't be reached
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DocumentComplianceUnavailableException("Documents service is unreachable."));

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.NotNull(result.Reason);
    }

    // =========================================================================
    // CSTD-24: Condition Gate Tests (24-N2)
    // =========================================================================

    [Fact]
    public async Task EvaluateAsync_ActivePendingConditionForTargetStage_BlocksStageAdvance()
    {
        // Arrange: active Approval condition required before Execution, status Pending
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = ConditionType.Approval,
            Title = "Scope sign-off",
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution
        };

        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EngagementCondition> { condition });

        // Documents are all satisfied
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified")
            });

        // Act: evaluating transition into Execution
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert: blocked specifically due to pending condition
        Assert.False(result.IsSatisfied);
        Assert.Contains("Approval condition 'Scope sign-off' is not yet satisfied (status: Pending).", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ActiveRejectedConditionForTargetStage_BlocksStageAdvance()
    {
        // Arrange: active Payment condition required before Execution, status Rejected
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = ConditionType.Payment,
            Title = "Upfront deposit",
            Status = ConditionStatus.Rejected,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution
        };

        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EngagementCondition> { condition });

        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified")
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("Payment condition 'Upfront deposit' is not yet satisfied (status: Rejected).", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ActiveSatisfiedConditionForTargetStage_Passes()
    {
        // Arrange: active Approval condition required before Execution is Satisfied
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = ConditionType.Approval,
            Title = "Scope sign-off",
            Status = ConditionStatus.Satisfied,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Execution
        };

        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EngagementCondition> { condition });

        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified")
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert: both conditions and documents satisfied
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ConditionForDifferentTargetStage_IsIgnored()
    {
        // Arrange: pending condition required before Closure (Stage 4), but we are entering Verification (Stage 2)
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = ConditionType.Approval,
            Title = "Final sign-off",
            Status = ConditionStatus.Pending,
            IsActive = true,
            RequiredBeforeStage = EngagementStage.Closure
        };

        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EngagementCondition> { condition });

        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Unverified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Unverified")
            });

        // Act: evaluating Verification
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        // Assert: Closure condition is ignored, Verification passes
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_InactiveCondition_IsIgnored()
    {
        // Arrange: condition is inactive (e.g. deactivated)
        var condition = new EngagementCondition
        {
            ConditionId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = ConditionType.Approval,
            Title = "Cancelled sign-off",
            Status = ConditionStatus.Pending,
            IsActive = false,
            RequiredBeforeStage = EngagementStage.Execution
        };

        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EngagementCondition> { condition });

        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Verified")
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert: inactive condition never blocks
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_ConditionServiceUnavailable_FailsClosedAndReturnsBlocked()
    {
        // Arrange: condition service throws exception
        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Condition service unavailable."));

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert: fails closed
        Assert.False(result.IsSatisfied);
        Assert.Contains("Unable to verify engagement conditions right now", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_UnapprovedRequirement_BlocksGateWithSpecificReason()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        using var db = new WorkflowDbContext(options);

        var engagement = new Engagement
        {
            EngagementId = _engagementId,
            TenantId = TenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Stage = EngagementStage.Onboarding,
            Status = EngagementStatus.Started
        };
        var requirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = "CompanyRegistrationNumber",
            Status = RequirementStatus.Requested, // NOT Approved
            StageNumber = 1
        };

        await db.Engagements.AddAsync(engagement);
        await db.Requirements.AddAsync(requirement);
        await db.SaveChangesAsync();

        var evaluatorWithDb = new GateEvaluator(
            _mockDocumentClient.Object,
            _mockConditionService.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GateEvaluator>.Instance,
            db);

        // Act: attempt to advance to DocumentCollection
        var result = await evaluatorWithDb.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("Required information 'CompanyRegistrationNumber' has not been provided/approved.", result.Reason);
        Assert.Contains(result.Requirements, r => !r.IsSatisfied && r.RequirementName == "CompanyRegistrationNumber");
    }

    [Fact]
    public async Task EvaluateAsync_ApprovedRequirement_SatisfiesRequirementGate()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        using var db = new WorkflowDbContext(options);

        var engagement = new Engagement
        {
            EngagementId = _engagementId,
            TenantId = TenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Stage = EngagementStage.Onboarding,
            Status = EngagementStatus.Started
        };
        var requirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = "CompanyRegistrationNumber",
            Status = RequirementStatus.Approved, // APPROVED
            StageNumber = 1
        };

        await db.Engagements.AddAsync(engagement);
        await db.Requirements.AddAsync(requirement);
        await db.SaveChangesAsync();

        var evaluatorWithDb = new GateEvaluator(
            _mockDocumentClient.Object,
            _mockConditionService.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GateEvaluator>.Instance,
            db);

        // Act
        var result = await evaluatorWithDb.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        // Assert
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
        Assert.Contains(result.Requirements, r => r.IsSatisfied && r.RequirementName == "CompanyRegistrationNumber");
    }

    [Fact]
    public async Task EvaluateAsync_LaterStageRequirement_DoesNotBlockCurrentStageGate()
    {
        // Arrange
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        using var db = new WorkflowDbContext(options);

        var engagement = new Engagement
        {
            EngagementId = _engagementId,
            TenantId = TenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Stage = EngagementStage.Onboarding, // Stage 1
            Status = EngagementStatus.Started
        };
        var futureRequirement = new Requirement
        {
            RequirementId = Guid.NewGuid(),
            EngagementId = _engagementId,
            TenantId = TenantId,
            Type = "ExecutionMilestoneSignoff",
            Status = RequirementStatus.Requested, // Unapproved, but belongs to Stage 4
            StageNumber = 4
        };

        await db.Engagements.AddAsync(engagement);
        await db.Requirements.AddAsync(futureRequirement);
        await db.SaveChangesAsync();

        var evaluatorWithDb = new GateEvaluator(
            _mockDocumentClient.Object,
            _mockConditionService.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GateEvaluator>.Instance,
            db);

        // Act: advancing to DocumentCollection (Stage 2)
        var result = await evaluatorWithDb.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        // Assert: future stage requirement does not block Stage 1 -> Stage 2 advance
        Assert.True(result.IsSatisfied);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_WithPrefetchedInputs_MatchesFetchingOverload_WithoutCallingDependencies()
    {
        // Arrange: KYC compliant but unverified, proof of address missing; one pending approval gating Verification
        var documents = new List<DocumentSummaryDto>
        {
            MakeDoc("KYC_PASSPORT", "Compliant", "Unverified")
        };
        var conditions = new List<EngagementCondition>
        {
            new()
            {
                ConditionId = Guid.NewGuid(),
                EngagementId = _engagementId,
                TenantId = TenantId,
                Type = ConditionType.Approval,
                Status = ConditionStatus.Pending,
                IsActive = true,
                RequiredBeforeStage = EngagementStage.Verification,
                Title = "Scope approval"
            }
        };

        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(documents);
        _mockConditionService
            .Setup(c => c.GetActiveConditionsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(conditions);

        var fetched = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);
        _mockDocumentClient.Invocations.Clear();
        _mockConditionService.Invocations.Clear();

        // Act
        var prefetched = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification, documents, conditions);

        // Assert
        Assert.False(prefetched.IsSatisfied);
        Assert.Equal(fetched.IsSatisfied, prefetched.IsSatisfied);
        Assert.Equal(fetched.Reason, prefetched.Reason);
        Assert.Equal(
            fetched.Requirements.Select(r => (r.RequirementName, r.IsSatisfied, r.Reason)),
            prefetched.Requirements.Select(r => (r.RequirementName, r.IsSatisfied, r.Reason)));
        _mockDocumentClient.Verify(c => c.GetDocumentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockConditionService.Verify(c => c.GetActiveConditionsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // =========================================================================
    // Open task gate: every task of the current or an earlier stage must be finished
    // =========================================================================

    private async Task<(WorkflowDbContext Db, GateEvaluator Evaluator)> CreateDbEvaluatorAsync(
        EngagementStage stage,
        params ClientAction[] actions)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new WorkflowDbContext(options);

        await db.Engagements.AddAsync(new Engagement
        {
            EngagementId = _engagementId,
            TenantId = TenantId,
            ClientId = "client-001",
            StaffId = "staff-001",
            Stage = stage,
            Status = EngagementStatus.Started
        });
        await db.ClientActions.AddRangeAsync(actions);
        await db.SaveChangesAsync();

        var evaluator = new GateEvaluator(
            _mockDocumentClient.Object,
            _mockConditionService.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GateEvaluator>.Instance,
            db);

        return (db, evaluator);
    }

    private ClientAction MakeTask(string title, string status, int stageNumber, string tenantId = TenantId) => new()
    {
        ActionId = Guid.NewGuid(),
        EngagementId = _engagementId,
        TenantId = tenantId,
        Title = title,
        Type = ClientActionType.CustomTask,
        Status = status,
        StageNumber = stageNumber,
        AssignedToRole = "Client"
    };

    [Theory]
    [InlineData(ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Rejected)]
    public async Task EvaluateAsync_OpenTaskInCurrentStage_BlocksAdvance(string status)
    {
        var (db, evaluator) = await CreateDbEvaluatorAsync(EngagementStage.Onboarding, MakeTask("Kickoff form", status, 1));
        using var _ = db;

        var result = await evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        Assert.False(result.IsSatisfied);
        Assert.Contains($"Task 'Kickoff form' is still {status}.", result.Reason);
    }

    [Theory]
    [InlineData(ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Cancelled)]
    public async Task EvaluateAsync_FinishedTasks_DoNotBlockAdvance(string status)
    {
        var (db, evaluator) = await CreateDbEvaluatorAsync(EngagementStage.Onboarding, MakeTask("Kickoff form", status, 1));
        using var _ = db;

        var result = await evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        Assert.True(result.IsSatisfied);
    }

    [Fact]
    public async Task EvaluateAsync_OpenTaskFromEarlierStage_StillBlocks_LaterStageTaskIgnored()
    {
        var (db, evaluator) = await CreateDbEvaluatorAsync(
            EngagementStage.DocumentCollection,
            MakeTask("Leftover stage 1 task", ClientActionStatus.Pending, 1),
            MakeTask("Stage 4 task", ClientActionStatus.Pending, 4));
        using var _ = db;
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Unverified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Unverified")
            });

        var result = await evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        Assert.False(result.IsSatisfied);
        Assert.Contains("Task 'Leftover stage 1 task' is still Pending.", result.Reason);
        Assert.DoesNotContain("Stage 4 task", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_RequirementAndConditionLinkedTasks_AreJudgedByTheirSource_NotAsTasks()
    {
        var requirementTask = MakeTask("Mirrored requirement task", ClientActionStatus.Pending, 1);
        requirementTask.LinkedRequirementId = Guid.NewGuid();
        var conditionTask = MakeTask("Condition task", ClientActionStatus.Pending, 1);
        conditionTask.SourceType = ClientActionSourceType.Condition;
        conditionTask.LinkedConditionId = Guid.NewGuid();

        var (db, evaluator) = await CreateDbEvaluatorAsync(EngagementStage.Onboarding, requirementTask, conditionTask);
        using var _ = db;

        var result = await evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        Assert.True(result.IsSatisfied);
    }

    [Fact]
    public async Task EvaluateAsync_OtherTenantsOpenTask_IsIgnored()
    {
        var (db, evaluator) = await CreateDbEvaluatorAsync(
            EngagementStage.Onboarding,
            MakeTask("Other tenant task", ClientActionStatus.Pending, 1, tenantId: "tenant-other"));
        using var _ = db;

        var result = await evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.DocumentCollection);

        Assert.True(result.IsSatisfied);
    }
}
