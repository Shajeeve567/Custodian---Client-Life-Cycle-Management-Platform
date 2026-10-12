using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

public class ConditionSatisfyEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public ConditionSatisfyEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [SkippableFact]
    public async Task Satisfy_Unauthenticated_Returns401()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");
        var client = _factory.CreateClient();

        var resp = await client.PostAsync(
            $"/api/engagements/{Guid.NewGuid()}/conditions/{Guid.NewGuid()}/satisfy", null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Satisfy_MismatchedTenant_Returns403()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));

        var resp = await client.PostAsync(
            $"/api/engagements/{Guid.NewGuid()}/conditions/{Guid.NewGuid()}/satisfy?tenantId={other}", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Satisfy_UnknownCondition_Returns404()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var resp = await client.PostAsync(
            $"/api/engagements/{Guid.NewGuid()}/conditions/{Guid.NewGuid()}/satisfy", null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
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
        catch { return false; }
    }
}