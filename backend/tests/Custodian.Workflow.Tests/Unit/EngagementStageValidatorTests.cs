using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for EngagementStageValidator.
/// We are testing that stage transitions are strictly sequential and forward-only, and that
/// progress percentage is derived correctly from stage, as specified in the CSTD-17 acceptance criteria!
/// </summary>
public class EngagementStageValidatorTests
{
    // ==========================================
    // 1. VALID STAGE TRANSITION TESTS
    // ==========================================

    [Theory]
    [InlineData(EngagementStage.Onboarding, EngagementStage.DocumentCollection)]
    [InlineData(EngagementStage.DocumentCollection, EngagementStage.Verification)]
    [InlineData(EngagementStage.Verification, EngagementStage.Execution)]
    [InlineData(EngagementStage.Execution, EngagementStage.Closure)]
    public void IsValidTransition_SequentialForwardStep_ShouldReturnTrue(EngagementStage current, EngagementStage next)
    {
        // Arrange & Act: Moving exactly one stage forward is always allowed
        bool result = EngagementStageValidator.IsValidTransition(current, next);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(EngagementStage.Onboarding)]
    [InlineData(EngagementStage.DocumentCollection)]
    [InlineData(EngagementStage.Verification)]
    [InlineData(EngagementStage.Execution)]
    [InlineData(EngagementStage.Closure)]
    public void IsValidTransition_SameStage_ShouldReturnTrue(EngagementStage stage)
    {
        // Arrange & Act: Setting stage to the exact same value should be idempotent and allowed
        bool result = EngagementStageValidator.IsValidTransition(stage, stage);

        // Assert
        Assert.True(result);
    }

    // ==========================================
    // 2. INVALID STAGE TRANSITION TESTS (Negative Tests)
    // ==========================================

    [Theory]
    [InlineData(EngagementStage.Onboarding, EngagementStage.Verification)]     // Skipping a stage
    [InlineData(EngagementStage.Onboarding, EngagementStage.Execution)]         // Skipping multiple stages
    [InlineData(EngagementStage.Onboarding, EngagementStage.Closure)]      // Skipping to the end
    [InlineData(EngagementStage.DocumentCollection, EngagementStage.Closure)] // Skipping ahead
    public void IsValidTransition_SkippingAhead_ShouldReturnFalse(EngagementStage current, EngagementStage next)
    {
        // Arrange & Act: Skipping stages is never allowed, no matter how far ahead
        bool result = EngagementStageValidator.IsValidTransition(current, next);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(EngagementStage.DocumentCollection, EngagementStage.Onboarding)]
    [InlineData(EngagementStage.Verification, EngagementStage.DocumentCollection)]
    [InlineData(EngagementStage.Closure, EngagementStage.Onboarding)]
    public void IsValidTransition_MovingBackwards_ShouldReturnFalse(EngagementStage current, EngagementStage next)
    {
        // Arrange & Act: Moving backwards is never allowed
        bool result = EngagementStageValidator.IsValidTransition(current, next);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(EngagementStage.Closure, EngagementStage.Onboarding)]
    [InlineData(EngagementStage.Closure, EngagementStage.DocumentCollection)]
    [InlineData(EngagementStage.Closure, EngagementStage.Verification)]
    [InlineData(EngagementStage.Closure, EngagementStage.Execution)]
    public void IsValidTransition_FromTerminalStage_ShouldReturnFalse(EngagementStage current, EngagementStage next)
    {
        // Arrange & Act: The final stage is terminal, no further transitions are allowed
        bool result = EngagementStageValidator.IsValidTransition(current, next);

        // Assert
        Assert.False(result);
    }

    // ==========================================
    // 3. PROGRESS PERCENTAGE DERIVATION TESTS
    // ==========================================

    [Theory]
    [InlineData(EngagementStage.Onboarding, 0)]
    [InlineData(EngagementStage.DocumentCollection, 25)]
    [InlineData(EngagementStage.Verification, 50)]
    [InlineData(EngagementStage.Execution, 75)]
    [InlineData(EngagementStage.Closure, 100)]
    public void GetProgressPercentage_EachStage_ShouldReturnExpectedPercentage(EngagementStage stage, int expectedPercentage)
    {
        // Arrange & Act: Progress percentage is a pure, deterministic function of stage
        int result = EngagementStageValidator.GetProgressPercentage(stage);

        // Assert
        Assert.Equal(expectedPercentage, result);
    }
}
