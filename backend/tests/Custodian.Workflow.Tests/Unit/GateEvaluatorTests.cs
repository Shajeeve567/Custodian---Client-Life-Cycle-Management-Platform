using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Custodian.Workflow.Services.Gates;
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
}
