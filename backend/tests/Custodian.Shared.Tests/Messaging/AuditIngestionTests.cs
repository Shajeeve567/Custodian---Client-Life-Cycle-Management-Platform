using Custodian.Shared.Messaging;
using Xunit;

namespace Custodian.Shared.Tests.Messaging;

public class AuditIngestionTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("<generate-a-random-secret>", false)]
    [InlineData("FILL_IN_HERE_FILL_IN_HERE_FILL_IN_HERE", false)]
    [InlineData("0123456789abcdef0123456789abcde", false)]  // 31 chars
    [InlineData("0123456789abcdef0123456789abcdef", true)]  // 32 chars
    public void IsUsableKey(string? key, bool expected)
    {
        Assert.Equal(expected, AuditIngestion.IsUsableKey(key));
    }
}
