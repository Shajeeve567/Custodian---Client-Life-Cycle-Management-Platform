using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Moq;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for GateEvaluator (CSTD-18). Mocks IDocumentComplianceClient so these run
/// without a real Documents service, matching the style used for KafkaAuditPublisherTests.
/// </summary>
public class GateEvaluatorTests
{
    private readonly Mock<IDocumentComplianceClient> _mockDocumentClient;
    private readonly GateEvaluator _evaluator;
    private readonly Guid _engagementId = Guid.NewGuid();
    private const string TenantId = "tenant-001";

    public GateEvaluatorTests()
    {
        _mockDocumentClient = new Mock<IDocumentComplianceClient>();
        _evaluator = new GateEvaluator(_mockDocumentClient.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<GateEvaluator>.Instance);
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
        Assert.Contains("KYC_PASSPORT", result.Reason);
        Assert.Contains("has not been submitted", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_RequiredDocumentNotCompliant_ReturnsBlocked()
    {
        // Arrange
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Pending", "Unverified"),
                MakeDoc("PROOF_OF_ADDRESS", "Pending", "Unverified"),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Verification);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("not yet compliant", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_CompliantDocument_WhenVerificationNotRequired_ReturnsSatisfied()
    {
        // Arrange: entering Verification only requires auto-compliance, not staff verification
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
    }

    [Fact]
    public async Task EvaluateAsync_CompliantButUnverifiedDocument_WhenVerificationRequired_ReturnsBlocked()
    {
        // Arrange: THE core CSTD-18 business rule — entering Execution requires staff
        // verification, and automatic compliance alone must not satisfy it.
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Unverified"),
                MakeDoc("PROOF_OF_ADDRESS", "Compliant", "Unverified"),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("compliant but not yet verified by staff", result.Reason);
    }

    [Fact]
    public async Task EvaluateAsync_CompliantAndVerifiedDocument_WhenVerificationRequired_ReturnsSatisfied()
    {
        // Arrange
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
    }

    [Fact]
    public async Task EvaluateAsync_OnlyOneOfTwoRequirementsSatisfied_ReturnsBlockedNamingTheMissingOne()
    {
        // Arrange: KYC_PASSPORT is fully verified, PROOF_OF_ADDRESS is missing entirely
        _mockDocumentClient
            .Setup(c => c.GetDocumentsAsync(_engagementId, TenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DocumentSummaryDto>
            {
                MakeDoc("KYC_PASSPORT", "Compliant", "Verified"),
            });

        // Act
        var result = await _evaluator.EvaluateAsync(_engagementId, TenantId, EngagementStage.Execution);

        // Assert
        Assert.False(result.IsSatisfied);
        Assert.Contains("PROOF_OF_ADDRESS", result.Reason);
        Assert.DoesNotContain("KYC_PASSPORT", result.Reason);
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
}
