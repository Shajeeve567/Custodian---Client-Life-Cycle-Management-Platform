using Custodian.Workflow.Services;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

public class StallEventDeduplicatorTests
{
    [Fact]
    public void FirstCall_NotYetFired()
    {
        var d = new StallEventDeduplicator();
        Assert.False(d.HasFired(Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow));
    }

    [Fact]
    public void AfterMarkFired_ReturnsTrue()
    {
        var d = new StallEventDeduplicator();
        var e = Guid.NewGuid(); var a = Guid.NewGuid(); var dl = DateTime.UtcNow;

        d.MarkFired(e, a, dl);

        Assert.True(d.HasFired(e, a, dl));
    }

    [Fact]
    public void DifferentDeadline_TreatedAsNewStall()
    {
        // Extending a deadline allows a fresh overdue event — that's the intent.
        var d = new StallEventDeduplicator();
        var e = Guid.NewGuid(); var a = Guid.NewGuid();

        d.MarkFired(e, a, DateTime.UtcNow);
        Assert.False(d.HasFired(e, a, DateTime.UtcNow.AddHours(1)));
    }
}