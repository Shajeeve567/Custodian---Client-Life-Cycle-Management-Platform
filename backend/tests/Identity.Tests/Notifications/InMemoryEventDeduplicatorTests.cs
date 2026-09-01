using Custodian.Identity.Services.Notifications;
using Xunit;

namespace Identity.Tests.Notifications;

public class InMemoryEventDeduplicatorTests
{
    [Fact]
    public void IsDuplicate_FirstTime_ShouldReturnFalse()
    {
        var deduplicator = new InMemoryEventDeduplicator();
        var result = deduplicator.IsDuplicate("event-1");
        Assert.False(result);
    }

    [Fact]
    public void IsDuplicate_SecondTime_ShouldReturnTrue()
    {
        var deduplicator = new InMemoryEventDeduplicator();
        deduplicator.IsDuplicate("event-1");
        var result = deduplicator.IsDuplicate("event-1");
        Assert.True(result);
    }

    [Fact]
    public void IsDuplicate_DifferentEvents_ShouldBothReturnFalse()
    {
        var deduplicator = new InMemoryEventDeduplicator();
        Assert.False(deduplicator.IsDuplicate("event-1"));
        Assert.False(deduplicator.IsDuplicate("event-2"));
        Assert.False(deduplicator.IsDuplicate("event-3"));
    }

    [Fact]
    public void IsDuplicate_EmptyOrNullEventId_ShouldReturnFalse()
    {
        var deduplicator = new InMemoryEventDeduplicator();
        Assert.False(deduplicator.IsDuplicate(""));
        Assert.False(deduplicator.IsDuplicate("   "));
    }
}
