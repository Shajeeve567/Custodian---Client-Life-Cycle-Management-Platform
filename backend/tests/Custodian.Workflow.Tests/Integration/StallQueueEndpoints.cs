using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

public class StallQueueEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public StallQueueEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task GetQueue_Unauthenticated_Returns401()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/stall-queue");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task GetQueue_NoStalls_ReturnsEmptyArray()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var items = await client.GetFromJsonAsync<List<StallQueueItemDto>>("/api/stall-queue");

        Assert.NotNull(items);
        Assert.Empty(items!);
    }

    [SkippableFact]
    public async Task GetQueue_MismatchedTenantQuery_Returns403()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));

        var response = await client.GetAsync($"/api/stall-queue?tenantId={otherTenant}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static bool ProbeMySql()
    {
        try
        {
            var cs = Environment.GetEnvironmentVariable("ConnectionStrings__AzureMySqlConnection")
                     ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                     ?? throw new InvalidOperationException("Set ConnectionStrings__Default.");
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