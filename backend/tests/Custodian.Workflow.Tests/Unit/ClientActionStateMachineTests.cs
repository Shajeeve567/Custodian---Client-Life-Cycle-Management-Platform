using Custodian.Workflow.Models;
using Custodian.Workflow.Services;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class ClientActionStateMachineTests
{
    [Theory]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Rejected)]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Cancelled)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Rejected)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Cancelled)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Cancelled)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Rejected)]
    public void CanTransition_ValidTransitions_ReturnsTrue(string from, string to)
    {
        // Act
        var result = ClientActionStateMachine.CanTransition(from, to);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(ClientActionStatus.Pending, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Uploaded, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Rejected, ClientActionStatus.Rejected)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Cancelled)]
    public void CanTransition_SameToSame_ReturnsTrueAsNoOp(string from, string to)
    {
        // Act
        var result = ClientActionStateMachine.CanTransition(from, to);

        // Assert
        Assert.True(result);
    }

    [Theory]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Completed, ClientActionStatus.Cancelled)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Pending)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Uploaded)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Completed)]
    [InlineData(ClientActionStatus.Cancelled, ClientActionStatus.Rejected)]
    public void CanTransition_DisallowedTransitions_ReturnsFalse(string from, string to)
    {
        // Act
        var result = ClientActionStateMachine.CanTransition(from, to);

        // Assert
        Assert.False(result);
    }

    [Theory]
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

    [Fact]
    public void EnsureCanTransition_SameToSame_DoesNotThrow()
    {
        // Act & Assert
        var ex = Record.Exception(() =>
            ClientActionStateMachine.EnsureCanTransition(ClientActionStatus.Completed, ClientActionStatus.Completed));

        Assert.Null(ex);
    }
}
