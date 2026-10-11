using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for EngagementLifecycleValidator.
/// We are testing that our status transition rules and physical deletion rules behave strictly as specified in the business rules!
/// </summary>
public class EngagementLifecycleValidatorTests
{
    // ==========================================
    // 1. DELETE PERMISSION TESTS (Subtask 3 Rule)
    // ==========================================

    [Fact]
    public void CanDelete_DraftEngagement_ShouldReturnTrue()
    {
        // Arrange & Act: Only Draft engagements can be physically deleted per acceptance criteria
        bool result = EngagementLifecycleValidator.CanDelete(EngagementStatus.Draft);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(EngagementStatus.Started)]
    [InlineData(EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Cancelled)]
    public void CanDelete_NonDraftEngagements_ShouldReturnFalse(EngagementStatus status)
    {
        // Arrange & Act: Started, Closed, or Cancelled engagements CANNOT be physically deleted
        bool result = EngagementLifecycleValidator.CanDelete(status);

        // Assert
        Assert.False(result);
    }

   
    // 2. VALID STATUS TRANSITION TESTS
    

    [Theory]
    [InlineData(EngagementStatus.Draft, EngagementStatus.Started)]
    [InlineData(EngagementStatus.Draft, EngagementStatus.Cancelled)]
    [InlineData(EngagementStatus.Started, EngagementStatus.Closed)]
    [InlineData(EngagementStatus.Started, EngagementStatus.Cancelled)]
    public void IsValidTransition_AllowedTransitions_ShouldReturnTrue(EngagementStatus current, EngagementStatus newStatus)
    {
        // Arrange & Act: Checking legal lifecycle flow (e.g. Draft -> Started, Started -> Closed)
        bool result = EngagementLifecycleValidator.IsValidTransition(current, newStatus);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidTransition_SameStatus_ShouldReturnTrue()
    {
        // Arrange & Act: Setting status to the exact same value should be idempotent and allowed
        bool result = EngagementLifecycleValidator.IsValidTransition(EngagementStatus.Started, EngagementStatus.Started);

        // Assert
        Assert.True(result);
    }

    // ==========================================
    // 3. INVALID STATUS TRANSITION TESTS (Negative Tests)
    // ==========================================

    [Theory]
    [InlineData(EngagementStatus.Closed, EngagementStatus.Draft)]     // Cannot re-open closed engagement to draft
    [InlineData(EngagementStatus.Closed, EngagementStatus.Started)]   // Cannot re-open closed engagement to started
    [InlineData(EngagementStatus.Cancelled, EngagementStatus.Started)]// Cannot revive cancelled engagement
    [InlineData(EngagementStatus.Cancelled, EngagementStatus.Closed)] // Cannot close a cancelled engagement
    public void IsValidTransition_IllegalTransitions_ShouldReturnFalse(EngagementStatus current, EngagementStatus newStatus)
    {
        // Arrange & Act: Testing illegal backwards or invalid status jumps
        bool result = EngagementLifecycleValidator.IsValidTransition(current, newStatus);

        // Assert
        Assert.False(result);
    }

    [Theory]
    [InlineData(EngagementStatus.Draft, true)]
    [InlineData(EngagementStatus.Started, true)]
    [InlineData(EngagementStatus.Closed, false)]
    [InlineData(EngagementStatus.Cancelled, false)]
    public void CanMutate_OnlyOpenStatusesAcceptMutations(EngagementStatus status, bool expected)
    {
        Assert.Equal(expected, EngagementLifecycleValidator.CanMutate(status));
    }

    [Theory]
    [InlineData(EngagementStatus.Draft, EngagementStatus.Started, true)]
    [InlineData(EngagementStatus.Started, EngagementStatus.Closed, true)]
    [InlineData(EngagementStatus.Started, EngagementStatus.Cancelled, true)]
    [InlineData(EngagementStatus.Closed, EngagementStatus.Started, false)]
    [InlineData(EngagementStatus.Cancelled, EngagementStatus.Started, false)]
    [InlineData(EngagementStatus.Closed, EngagementStatus.Closed, true)] // idempotent no-op
    public void IsValidTransition_EnforcesLifecycle(EngagementStatus from, EngagementStatus to, bool expected)
    {
        Assert.Equal(expected, EngagementLifecycleValidator.IsValidTransition(from, to));
    }

    [Fact]
    public void CanDelete_OnlyDraft()
    {
        Assert.True(EngagementLifecycleValidator.CanDelete(EngagementStatus.Draft));
        Assert.False(EngagementLifecycleValidator.CanDelete(EngagementStatus.Started));
        Assert.False(EngagementLifecycleValidator.CanDelete(EngagementStatus.Closed));
        Assert.False(EngagementLifecycleValidator.CanDelete(EngagementStatus.Cancelled));
    }
}
