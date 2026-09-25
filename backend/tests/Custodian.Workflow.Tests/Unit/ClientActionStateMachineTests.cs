using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientActionStateMachineTests
{
    /// <summary>
    /// Exhaustive table-driven test of all 25 (from, to) combinations for ClientAction lifecycle.
    /// </summary>
    [Theory]
    // From Pending (5)
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Pending, true)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Uploaded, true)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Completed, true)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Rejected, true)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Cancelled, true)]

    // From Uploaded (5)
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Pending, false)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Uploaded, true)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Completed, true)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Rejected, true)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Cancelled, true)]

    // From Rejected (5)
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Pending, true)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Uploaded, true)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Completed, true)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Rejected, true)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Cancelled, true)]

    // From Completed (5)
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Pending, false)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Uploaded, false)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Completed, true)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Rejected, true)] // Requirement review rejected reopens action
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Cancelled, false)]

    // From Cancelled (5 - terminal)
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Pending, false)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Uploaded, false)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Completed, false)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Rejected, false)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Cancelled, true)]
    public void CanTransition_AllCombinations_MatchesExpectedTable(string from, string to, bool expected)
    {
        // Act
        var result = ClientActionStateMachine.CanTransition(from, to);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Cancelled)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Rejected)]
    public void EnsureCanTransition_DisallowedTransitions_ThrowsInvalidOperationException(string from, string to)
    {
        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ClientActionStateMachine.EnsureCanTransition(from, to));

        Assert.Contains(from, ex.Message);
        Assert.Contains(to, ex.Message);
    }

    [Theory]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Rejected)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Cancelled)]
    public void EnsureCanTransition_SameToSame_DoesNotThrow(string from, string to)
    {
        // Act & Assert
        var ex = Record.Exception(() =>
            ClientActionStateMachine.EnsureCanTransition(from, to));

        Assert.Null(ex);
    }
}
