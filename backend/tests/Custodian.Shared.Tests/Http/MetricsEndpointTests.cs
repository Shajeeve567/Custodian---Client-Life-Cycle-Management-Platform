using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Custodian.Shared.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Custodian.Shared.Tests.Http;

/// <summary>/metrics is open locally, Basic-auth protected when a scrape key is set, and absent otherwise.</summary>
public class MetricsEndpointTests
{
    private const string Key = "test-scrape-key-0123456789abcdef";

    private static async Task<HttpClient> StartAsync(string environment, string? scrapeKey = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (scrapeKey != null)
        {
            builder.Configuration[MetricsExtensions.ScrapeKeyConfig] = scrapeKey;
        }

        var app = builder.Build();
        app.UseCustodianMetrics();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        return app.GetTestClient();
    }

    private static AuthenticationHeaderValue Basic(string user, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task NoKey_Locally_ServesMetricsWithoutCredentials(string environment)
    {
        var client = await StartAsync(environment);
        await client.GetAsync("/ping");

        var response = await client.GetAsync(MetricsExtensions.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("http_requests_received_total", body);
        Assert.Contains("process_cpu_seconds_total", body);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NoKey_WhenDeployed_HasNoMetricsEndpoint(string environment)
    {
        var client = await StartAsync(environment);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(MetricsExtensions.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);
    }

    [Fact]
    public async Task WithKey_CorrectBasicCredentials_ServesMetrics()
    {
        var client = await StartAsync("Production", Key);
        var request = new HttpRequestMessage(HttpMethod.Get, MetricsExtensions.Path);
        request.Headers.Authorization = Basic(MetricsExtensions.ScrapeUser, Key);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("http_request_duration_seconds", await response.Content.ReadAsStringAsync());
    }

    public static TheoryData<AuthenticationHeaderValue?> RejectedCredentials => new()
    {
        null,
        new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"prometheus:wrong-{Key}"))),
        new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{Key}"))),
        new AuthenticationHeaderValue("Bearer", Key),
        new AuthenticationHeaderValue("Basic", "not-base64!")
    };

    [Theory]
    [MemberData(nameof(RejectedCredentials))]
    public async Task WithKey_MissingOrWrongCredentials_Returns401(AuthenticationHeaderValue? authorization)
    {
        var client = await StartAsync("Production", Key);
        var request = new HttpRequestMessage(HttpMethod.Get, MetricsExtensions.Path);
        request.Headers.Authorization = authorization;

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Basic", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task WithKey_OtherEndpoints_NeedNoScrapeCredentials()
    {
        var client = await StartAsync("Production", Key);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);
    }
}
