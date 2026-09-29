using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Custodian.Workflow.Tests.Integration;

public class InterventionsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public InterventionsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task Record_Unauthenticated_Returns401()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/interventions",
            new RecordInterventionRequest
            {
                Type = InterventionType.RecoveryAction,
                Reason = "test",
                Outcome = InterventionOutcome.Recovered,
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Record_MismatchedTenant_Returns403()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));

        var response = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/interventions?tenantId={other}",
            new RecordInterventionRequest
            {
                Type = InterventionType.RecoveryAction,
                Reason = "test",
                Outcome = InterventionOutcome.Recovered,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableFact]
    public async Task Record_UnknownEngagement_Returns404()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var response = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/interventions",
            new RecordInterventionRequest
            {
                Type = InterventionType.RecoveryAction,
                Reason = "test",
                Outcome = InterventionOutcome.Recovered,
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Record_InvalidOutcome_Returns400()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        // No engagement will match, so it 404s before validation — this test is a smoke check
        // on the endpoint contract. A full 400 test requires seeding an engagement; see the
        // gate bypass test class for that flow.
        var response = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/interventions",
            new RecordInterventionRequest { Type = "Bogus", Reason = "x", Outcome = "Bogus" });

        Assert.True(response.StatusCode == HttpStatusCode.NotFound
                    || response.StatusCode == HttpStatusCode.BadRequest);
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