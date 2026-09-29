using Custodian.Shared.Http;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Shared.Tests.Http;

/// <summary>M6: any origin is allowed only in Development/Testing; deployed services need an explicit list.</summary>
public class CorsPolicyTests
{
    private const string Frontend = "https://custodian.vercel.app";
    private const string Attacker = "https://evil.example";

    private static CorsPolicy PolicyFor(string environment, params string[] origins)
    {
        var settings = new Dictionary<string, string?>();
        for (var i = 0; i < origins.Length; i++)
        {
            settings[$"Cors:AllowedOrigins:{i}"] = origins[i];
        }

        var services = new ServiceCollection();
        services.AddCustodianCors(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new FakeEnvironment(environment));
        var options = services.BuildServiceProvider().GetRequiredService<IOptions<CorsOptions>>().Value;
        return options.GetPolicy(options.DefaultPolicyName)!;
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void NoOriginsConfigured_InDevelopmentOrTesting_AllowsAnyOrigin(string environment)
    {
        Assert.True(PolicyFor(environment).AllowAnyOrigin);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void NoOriginsConfigured_WhenDeployed_AllowsNoOrigin(string environment)
    {
        var policy = PolicyFor(environment);

        Assert.False(policy.AllowAnyOrigin);
        Assert.False(policy.IsOriginAllowed(Frontend));
        Assert.False(policy.IsOriginAllowed(Attacker));
    }

    [Fact]
    public void ConfiguredOrigins_AreTheOnlyOnesAllowed_AndTheQueueTotalHeaderIsReadable()
    {
        var policy = PolicyFor("Production", Frontend + "/");

        Assert.Contains(Frontend, policy.Origins); // trailing slash trimmed
        Assert.DoesNotContain(Attacker, policy.Origins);
        Assert.Contains("X-Total-Count", policy.ExposedHeaders);
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
