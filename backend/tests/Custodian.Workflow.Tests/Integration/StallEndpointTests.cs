using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

public class StallEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public StallEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task GetStall_Unauthenticated_Returns401()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/stall");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task GetStall_NonexistentEngagement_Returns404()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(Guid.NewGuid()));
        var response = await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/stall");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static bool ProbeMySql()
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__AzureMySqlConnection")
                 ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default");

        if (string.IsNullOrWhiteSpace(cs))
            return false;

        try
        {
            using var conn = new MySqlConnector.MySqlConnection(cs);
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }
}