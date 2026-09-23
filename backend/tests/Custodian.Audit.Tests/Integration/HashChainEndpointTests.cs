using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Custodian.Audit.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Custodian.Audit.Tests.Integration;

/// <summary>
/// Black-box tests through the real Audit pipeline: routing, JWT auth,
/// tenant resolution, service, EF, MySQL. The tamper case runs a raw SQL
/// UPDATE against audit_db to prove append-only-tamper detection — the
/// headline acceptance criterion for CSTD-40.
/// Requires a reachable MySQL with audit_db migrated. Skips otherwise.
/// </summary>
public class HashChainEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public HashChainEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task Verify_EmptyTenant_ReturnsVerifiedTrueZero()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var client = _factory.CreateClient();
        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);

        var result = await client.GetFromJsonAsync<ChainVerificationResult>("/api/audit-events/verify");

        Assert.NotNull(result);
        Assert.True(result!.IsVerified);
        Assert.Equal(0, result.Count);
    }

    [SkippableFact]
    public async Task PostThreeEvents_ThenVerify_ChainIsValid()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);

        var (e1, e2, e3) = await PostThreeAsync(client);

        Assert.Equal(e1.SequenceNumber + 1, e2.SequenceNumber);
        Assert.Equal(e2.SequenceNumber + 1, e3.SequenceNumber);

        Assert.Equal(new string('0', 64), e1.PreviousHash);
        Assert.Equal(e1.Hash, e2.PreviousHash);
        Assert.Equal(e2.Hash, e3.PreviousHash);

        var result = await client.GetFromJsonAsync<ChainVerificationResult>("/api/audit-events/verify");
        Assert.NotNull(result);
        Assert.True(result!.IsVerified);
        Assert.Equal(3, result.Count);
        Assert.Null(result.BrokenAtEventId);
    }

    [SkippableFact]
    public async Task TamperFirstEventPayload_VerifyBreaksAtFirstEvent()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);

        var (e1, _, _) = await PostThreeAsync(client);

        Tamper("UPDATE events SET payload = '{\"step\":999}' WHERE event_id = @id", e1.EventId);

        var result = await client.GetFromJsonAsync<ChainVerificationResult>("/api/audit-events/verify");

        Assert.NotNull(result);
        Assert.False(result!.IsVerified);
        Assert.Equal(e1.EventId, result.BrokenAtEventId);
        Assert.Equal("stored hash does not match recomputed hash", result.Reason);
    }

    [SkippableFact]
    public async Task TamperSecondEventPreviousHash_VerifyBreaksAtSecondEvent()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);

        var (_, e2, _) = await PostThreeAsync(client);

        Tamper("UPDATE events SET previous_hash = REPEAT('f', 64) WHERE event_id = @id", e2.EventId);

        var result = await client.GetFromJsonAsync<ChainVerificationResult>("/api/audit-events/verify");

        Assert.NotNull(result);
        Assert.False(result!.IsVerified);
        Assert.Equal(e2.EventId, result.BrokenAtEventId);
        Assert.Equal("previous_hash does not match the prior event's hash", result.Reason);
    }

    // ---------- helpers ----------

    private HttpClient BuildClient(string tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    private static async Task<(AuditEventResponse, AuditEventResponse, AuditEventResponse)> PostThreeAsync(HttpClient client)
    {
        var engagementId = Guid.NewGuid();
        var results = new List<AuditEventResponse>();

        for (var i = 1; i <= 3; i++)
        {
            var body = new CreateAuditEventRequest
            {
                EngagementId = engagementId,
                Actor = "integration-tester",
                Type = "Genesis",
                Payload = $"{{\"step\":{i}}}",
            };

            var response = await client.PostAsJsonAsync("/api/audit-events", body);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<AuditEventResponse>();
            Assert.NotNull(dto);
            results.Add(dto!);
        }

        return (results[0], results[1], results[2]);
    }

    private static void Tamper(string sql, Guid eventId)
    {
        var cs = GetConnectionString();
        using var conn = new MySqlConnector.MySqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", eventId.ToString());
        cmd.ExecuteNonQuery();
    }

    private static bool ProbeMySql()
    {
        try
        {
            using var conn = new MySqlConnector.MySqlConnection(GetConnectionString());
            conn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetConnectionString()
    {
        return Environment.GetEnvironmentVariable("ConnectionStrings__AzureMySqlConnection")
               ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
               ?? throw new InvalidOperationException(
                   "Set ConnectionStrings__Default (or AzureMySqlConnection) to run integration tests.");
    }
}