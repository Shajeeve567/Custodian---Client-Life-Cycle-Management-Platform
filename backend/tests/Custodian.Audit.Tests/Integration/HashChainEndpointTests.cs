using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Shared.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Custodian.Audit.Tests.Integration;

/// <summary>
/// Black-box tests through the real Audit pipeline: routing, JWT auth, tenant
/// resolution, service, EF, MySQL. Tamper tests run raw SQL against audit_db to
/// prove append-only tamper detection — the headline acceptance criterion for
/// CSTD-40. Chains are scoped per (tenant, engagement).
/// Requires a reachable MySQL with audit_db migrated. Skips otherwise.
/// </summary>
public class HashChainEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    // Audit writes are service-only: the test host is given an ingestion key and the client sends it.
    private const string IngestionKey = "integration-test-ingestion-key-0123456789";

    /// <summary>
    /// The one GUID the stubbed access client refuses. Any other GUID is treated as
    /// "caller may access". This is enough to test the 404 path without a live Workflow.
    /// </summary>
    private static readonly Guid DeniedEngagementId = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    public HashChainEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting(AuditIngestion.ConfigKey, IngestionKey);
            // Verify now calls the Workflow access client for every caller (not just Staff).
            // Stub it so these tests run without Workflow. Default policy: allow every
            // engagement except DeniedEngagementId.
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IEngagementAccessClient>();
                services.AddSingleton<IEngagementAccessClient>(
                    new StubEngagementAccessClient(id => id != DeniedEngagementId));
            });
        });
    }

    [SkippableFact]
    public async Task Verify_UnknownEngagement_Returns404()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);

        // The stub denies this engagement — verifying must 404, not
        // report "verified with 0 events", which would manufacture proof
        // of an engagement that does not exist.
        var response = await client.GetAsync(
            $"/api/audit-events/verify?engagementId={DeniedEngagementId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Verify_KnownEngagementWithNoEvents_ReturnsVerifiedTrueZero()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);
        var engagementId = Guid.NewGuid();   // stub allows everything except DeniedEngagementId

        var result = await VerifyAsync(client, engagementId);

        Assert.NotNull(result);
        Assert.True(result!.IsVerified);
        Assert.Equal(0, result.Count);
        Assert.Equal(engagementId, result.EngagementId);
    }

    [SkippableFact]
    public async Task PostThreeEvents_ThenVerify_ChainIsValid()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);

        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = await PostThreeAsync(client, engagementId);

        Assert.Equal(e1.SequenceNumber + 1, e2.SequenceNumber);
        Assert.Equal(e2.SequenceNumber + 1, e3.SequenceNumber);

        Assert.Equal(new string('0', 64), e1.PreviousHash);
        Assert.Equal(e1.Hash, e2.PreviousHash);
        Assert.Equal(e2.Hash, e3.PreviousHash);

        var result = await VerifyAsync(client, engagementId);

        Assert.NotNull(result);
        Assert.True(result!.IsVerified);
        Assert.Equal(3, result.Count);
        Assert.Null(result.BrokenAtEventId);
    }

    /// <summary>
    /// Regression for BUG-CSTD40-001: two engagements under the same tenant each
    /// start from genesis independently. Engagement B's first event must NOT chain
    /// off Engagement A's latest hash.
    /// </summary>
    [SkippableFact]
    public async Task TwoEngagementsSameTenant_EachStartsFromGenesis_Independently()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);

        var engA = Guid.NewGuid();
        var engB = Guid.NewGuid();

        var (a1, a2, a3) = await PostThreeAsync(client, engA);

        // First event of engagement B — should use genesis, not a3.Hash
        var b1 = await PostOneAsync(client, engB, step: 1);

        Assert.Equal(new string('0', 64), b1.PreviousHash);
        Assert.NotEqual(a3.Hash, b1.PreviousHash);

        // Both engagements verify independently, each with its own chain length
        var resultA = await VerifyAsync(client, engA);
        var resultB = await VerifyAsync(client, engB);

        Assert.NotNull(resultA);
        Assert.NotNull(resultB);
        Assert.True(resultA!.IsVerified);
        Assert.True(resultB!.IsVerified);
        Assert.Equal(3, resultA.Count);
        Assert.Equal(1, resultB.Count);
        Assert.Equal(engA, resultA.EngagementId);
        Assert.Equal(engB, resultB.EngagementId);
    }

    [SkippableFact]
    public async Task TamperFirstEventPayload_VerifyBreaksAtFirstEvent()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);
        var engagementId = Guid.NewGuid();

        var (e1, _, _) = await PostThreeAsync(client, engagementId);

        Tamper("UPDATE events SET payload = '{\"step\":999}' WHERE event_id = @id", e1.EventId);

        var result = await VerifyAsync(client, engagementId);

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
        var engagementId = Guid.NewGuid();

        var (_, e2, _) = await PostThreeAsync(client, engagementId);

        Tamper("UPDATE events SET previous_hash = REPEAT('f', 64) WHERE event_id = @id", e2.EventId);

        var result = await VerifyAsync(client, engagementId);

        Assert.NotNull(result);
        Assert.False(result!.IsVerified);
        Assert.Equal(e2.EventId, result.BrokenAtEventId);
        Assert.Equal("previous_hash does not match the prior event's hash", result.Reason);
    }

    /// <summary>
    /// CSTD-41 acceptance criterion: verification never mutates event data.
    /// Snapshot every column before and after two verify calls; assert byte-identical.
    /// </summary>
    [SkippableFact]
    public async Task Verify_DoesNotMutateEventRows()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = "tenant-integ-" + Guid.NewGuid().ToString("N");
        var client = BuildClient(tenant);
        var engagementId = Guid.NewGuid();

        await PostThreeAsync(client, engagementId);

        var before = SnapshotRows(tenant, engagementId);
        Assert.Equal(3, before.Count);

        // Verify twice — the second call must see the same state as the first.
        await VerifyAsync(client, engagementId);
        await VerifyAsync(client, engagementId);

        var after = SnapshotRows(tenant, engagementId);

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].EventId, after[i].EventId);
            Assert.Equal(before[i].SequenceNumber, after[i].SequenceNumber);
            Assert.Equal(before[i].Hash, after[i].Hash);
            Assert.Equal(before[i].PreviousHash, after[i].PreviousHash);
            Assert.Equal(before[i].Payload, after[i].Payload);
        }
    }

    // ---------- helpers ----------

    private HttpClient BuildClient(string tenant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AuditIngestion.HeaderName, IngestionKey);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant);
        return client;
    }

    private static async Task<ChainVerificationResult?> VerifyAsync(HttpClient client, Guid engagementId)
    {
        return await client.GetFromJsonAsync<ChainVerificationResult>(
            $"/api/audit-events/verify?engagementId={engagementId}");
    }

    private static async Task<(AuditEventResponse, AuditEventResponse, AuditEventResponse)> PostThreeAsync(
        HttpClient client, Guid engagementId)
    {
        var e1 = await PostOneAsync(client, engagementId, step: 1);
        var e2 = await PostOneAsync(client, engagementId, step: 2);
        var e3 = await PostOneAsync(client, engagementId, step: 3);
        return (e1, e2, e3);
    }

    private static async Task<AuditEventResponse> PostOneAsync(HttpClient client, Guid engagementId, int step)
    {
        var body = new CreateAuditEventRequest
        {
            EngagementId = engagementId,
            Actor = "integration-tester",
            Type = "Genesis",
            Payload = $"{{\"step\":{step}}}",
        };

        var response = await client.PostAsJsonAsync("/api/audit-events", body);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.NotNull(dto);
        return dto!;
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

    private static List<(Guid EventId, long SequenceNumber, string? Hash, string? PreviousHash, string Payload)> SnapshotRows(
        string tenant, Guid engagementId)
    {
        var cs = GetConnectionString();
        using var conn = new MySqlConnector.MySqlConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT event_id, sequence_number, hash, previous_hash, payload
            FROM events
            WHERE tenant_id = @tenant AND engagement_id = @engagement
            ORDER BY sequence_number";
        cmd.Parameters.AddWithValue("@tenant", TenantStringToGuid(tenant));
        cmd.Parameters.AddWithValue("@engagement", engagementId.ToString());

        var rows = new List<(Guid, long, string?, string?, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }
        return rows;
    }

    /// <summary>
    /// Mirrors the Audit service's tenant-id normalisation: non-Guid tenant
    /// strings are hashed (SHA-256, first 16 bytes) into a deterministic Guid
    /// before being persisted in the events.tenant_id column.
    /// </summary>
    private static Guid TenantStringToGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.Empty;
        if (Guid.TryParse(value, out var parsed)) return parsed;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
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

    /// <summary>
    /// Test-only stub of <see cref="IEngagementAccessClient"/>. Verify calls this
    /// for every caller now, so the test host needs a deterministic replacement
    /// instead of reaching the live Workflow service.
    /// </summary>
    private sealed class StubEngagementAccessClient : IEngagementAccessClient
    {
        private readonly Func<Guid, bool> _predicate;

        public StubEngagementAccessClient(Func<Guid, bool> predicate) => _predicate = predicate;

        public Task<bool> CanAccessEngagementAsync(Guid engagementId, Guid tenantId, CancellationToken ct = default)
            => Task.FromResult(_predicate(engagementId));

        public Task<IReadOnlySet<Guid>> GetAccessibleEngagementIdsAsync(Guid tenantId, CancellationToken ct = default)
            => Task.FromResult((IReadOnlySet<Guid>)new HashSet<Guid>());
    }
}