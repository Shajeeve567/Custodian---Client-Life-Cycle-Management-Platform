using Custodian.Shared.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Custodian.Shared.Tests.Auth;

/// <summary>
/// C6: the development signing key is committed in a public repository, so a service running
/// outside Development/Testing with it would accept tokens minted by anyone.
/// </summary>
public class JwtSigningKeyGuardTests
{
    private const string RealKey = "3f9c1b7e0a4d8e2f6b5c9a1d7e3f0b8c4a6d2e9f1b7c5a3e8d0f6b2c4a9e1d7f";

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DevelopmentKey_OutsideDevelopmentOrTesting_FailsStartup(string environment)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JwtSigningKeyGuard.Validate(JwtSigningKeyGuard.CommittedDevelopmentKey, environment));
        Assert.Contains("public development key", ex.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void DevelopmentKey_InDevelopmentOrTesting_IsAllowed(string environment)
    {
        JwtSigningKeyGuard.Validate(JwtSigningKeyGuard.CommittedDevelopmentKey, environment);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FILL_IN_HERE_minimum_64_chars_padding_padding_padding")] // .env.example placeholder
    [InlineData("too-short-key")]
    public void MissingPlaceholderOrShortKey_FailsInEveryEnvironment(string? key)
    {
        Assert.Throws<InvalidOperationException>(() => JwtSigningKeyGuard.Validate(key, "Development"));
        Assert.Throws<InvalidOperationException>(() => JwtSigningKeyGuard.Validate(key, "Production"));
    }

    [Fact]
    public void RealKey_IsAllowedInProduction()
    {
        JwtSigningKeyGuard.Validate(RealKey, "Production");
    }

    [Fact]
    public void AddJwtAuthentication_RunsTheGuard()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = "custodian-identity",
                ["Jwt:Audience"] = "custodian-services",
                ["Jwt:SigningKey"] = JwtSigningKeyGuard.CommittedDevelopmentKey,
                ["Jwt:ExpiryMinutes"] = "60"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddJwtAuthentication(configuration, new FakeEnvironment("Production")));
        new ServiceCollection().AddJwtAuthentication(configuration, new FakeEnvironment("Development"));
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
