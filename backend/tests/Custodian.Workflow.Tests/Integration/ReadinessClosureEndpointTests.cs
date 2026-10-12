using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// CSTD-20 black-box tests through the real Workflow pipeline. The gate evaluator is
/// stubbed so the Documents service is not required and the readiness outcome is
/// deterministic across runs. Requires MySQL with workflow_db migrated.
/// </summary>
public class ReadinessClosureEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private static readonly bool DbReachable = ProbeMySql();

    public ReadinessClosureEndpointTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    // ---------- auth / tenant ----------

    [SkippableFact]
    public async Task Readiness_Unauthenticated_Returns401()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var client = _baseFactory.CreateClient();
        var resp = await client.GetAsync($"/api/Engagements/{Guid.NewGuid()}/readiness");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Readiness_MismatchedTenant_Returns403()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        var client = BuildClient(tenant, gateSatisfied: true);
        var resp = await client.GetAsync($"/api/Engagements/{Guid.NewGuid()}/readiness?tenantId={attacker}");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Readiness_UnknownEngagement_Returns404()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var client = BuildClient(Guid.NewGuid(), gateSatisfied: true);
        var resp = await client.GetAsync($"/api/Engagements/{Guid.NewGuid()}/readiness");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ---------- readiness states ----------

    [SkippableFact]
    public async Task Readiness_DraftEngagement_ReturnsNotStarted()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateDraftEngagementAsync(client, tenant);

        var r = await client.GetFromJsonAsync<ReadinessResponse>($"/api/Engagements/{engId}/readiness");

        Assert.NotNull(r);
        Assert.False(r!.IsReady);
        Assert.Equal("NotStarted", r.State);
    }

    [SkippableFact]
    public async Task Readiness_ReadyAtClosure_ReturnsReady()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        var r = await client.GetFromJsonAsync<ReadinessResponse>($"/api/Engagements/{engId}/readiness");

        Assert.NotNull(r);
        Assert.True(r!.IsReady);
        Assert.Equal("Ready", r.State);
        Assert.Empty(r.Blockers);
    }

    [SkippableFact]
    public async Task Readiness_GateBlocked_ReturnsNotReadyWithBlockers()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        // Build at closure with gate satisfied to reach the stage, then flip the stub to blocked
        var clientOk = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(clientOk, tenant);

        var clientBlocked = BuildClient(tenant, gateSatisfied: false);
        var r = await clientBlocked.GetFromJsonAsync<ReadinessResponse>($"/api/Engagements/{engId}/readiness");

        Assert.NotNull(r);
        Assert.False(r!.IsReady);
        Assert.Equal("NotReady", r.State);
        Assert.NotEmpty(r.Blockers);
    }

    // ---------- handoff ----------

    [SkippableFact]
    public async Task Handoff_NotReady_Returns409()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: false);
        var engId = await CreateReadyAtClosureEngagementAsync(BuildClient(tenant, true), tenant);

        var resp = await client.PostAsJsonAsync($"/api/Engagements/{engId}/handoff",
            new HandoffEngagementRequest { Summary = "attempt" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Handoff_Ready_Returns200AndPersistsContext()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        var resp = await client.PostAsJsonAsync($"/api/Engagements/{engId}/handoff",
            new HandoffEngagementRequest { Summary = "Handed to delivery team." });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<EngagementResponse>();
        Assert.NotNull(body);
        Assert.Equal("Handed to delivery team.", body!.HandoffSummary);
        Assert.False(string.IsNullOrWhiteSpace(body.HandedOffBy));
        Assert.NotNull(body.HandedOffAtUtc);
    }

    // ---------- closure ----------

    [SkippableFact]
    public async Task Close_NotReady_Returns409()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var engId = await CreateReadyAtClosureEngagementAsync(BuildClient(tenant, true), tenant);
        var blocked = BuildClient(tenant, gateSatisfied: false);

        var resp = await blocked.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "attempt" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Close_Ready_Returns200WithClosedState()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        var resp = await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "Completed and delivered." });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<EngagementResponse>();
        Assert.NotNull(body);
        Assert.Equal("Closed", body!.Status);
        Assert.Equal("Completed and delivered.", body.ClosureSummary);
        Assert.NotNull(body.ClosedAt);
        Assert.False(string.IsNullOrWhiteSpace(body.ClosedBy));
    }

    [SkippableFact]
    public async Task Close_AlreadyClosed_IsIdempotent()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "First." });

        var second = await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "Second." });
        second.EnsureSuccessStatusCode();

        var body = await second.Content.ReadFromJsonAsync<EngagementResponse>();
        Assert.NotNull(body);
        Assert.Equal("Closed", body!.Status);
        Assert.Equal("First.", body.ClosureSummary); // unchanged
    }

    // ---------- post-closure guards ----------

    [SkippableFact]
    public async Task PostClosure_ActionMutation_Returns409()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        // Close it
        await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "closed" });

        // Try to create a new action
        var resp = await client.PostAsJsonAsync($"/api/engagements/{engId}/actions",
            new CreateClientActionDto
            {
                Title = "Post-closure attempt",
                Type = "CustomTask",
                Source = "Test",
                StageNumber = 1,
                AssignedToRole = "Client",
            });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [SkippableFact]
    public async Task PostClosure_ConditionMutation_Returns409()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "closed" });

        var resp = await client.PostAsJsonAsync($"/api/engagements/{engId}/conditions",
            new AttachConditionDto
            {
                Type = "Approval",
                RequiredBeforeStage = EngagementStage.Closure,
                Title = "Post-closure condition",
            });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [SkippableFact]
    public async Task PostClosure_MeetingMutation_Returns409()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "closed" });

        var resp = await client.PostAsJsonAsync($"/api/engagements/{engId}/meetings",
            new CreateMeetingRequest
            {
                Type = "Normal",
                Purpose = "Post-closure attempt",
                ScheduledAtUtc = DateTime.UtcNow.AddDays(1),
            });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    [SkippableFact]
    public async Task PostClosure_StageAdvance_Returns409()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = BuildClient(tenant, gateSatisfied: true);
        var engId = await CreateReadyAtClosureEngagementAsync(client, tenant);

        await client.PostAsJsonAsync($"/api/Engagements/{engId}/close",
            new CloseEngagementRequest { Summary = "closed" });

        var resp = await client.PutAsJsonAsync($"/api/Engagements/{engId}/stage",
            new UpdateEngagementStageRequest { TenantId = tenant.ToString(), Stage = "Closure" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // ---------- helpers ----------

    private HttpClient BuildClient(Guid tenant, bool gateSatisfied)
    {
        var factory = _baseFactory.WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IGateEvaluator>();
                services.AddSingleton<IGateEvaluator>(new StubGateEvaluator(gateSatisfied));
            }));

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());
        return client;
    }

    private static async Task<Guid> CreateDraftEngagementAsync(HttpClient client, Guid tenant)
    {
        var resp = await client.PostAsJsonAsync("/api/Engagements",
            new CreateEngagementRequest
            {
                TenantId = tenant.ToString(),
                ClientId = Guid.NewGuid().ToString(),
                StaffId = Guid.NewGuid().ToString(),
            });
        resp.EnsureSuccessStatusCode();
        var eng = await resp.Content.ReadFromJsonAsync<EngagementResponse>();
        return eng!.EngagementId;
    }

    private static async Task<Guid> CreateReadyAtClosureEngagementAsync(HttpClient client, Guid tenant)
    {
        var engId = await CreateDraftEngagementAsync(client, tenant);

        var start = await client.PutAsJsonAsync($"/api/Engagements/{engId}/status",
            new UpdateEngagementStatusRequest { TenantId = tenant.ToString(), Status = "Started" });
        start.EnsureSuccessStatusCode();

        foreach (var stage in new[] { "DocumentCollection", "Verification", "Execution", "Closure" })
        {
            var resp = await client.PutAsJsonAsync($"/api/Engagements/{engId}/stage",
                new UpdateEngagementStageRequest { TenantId = tenant.ToString(), Stage = stage });
            resp.EnsureSuccessStatusCode();
        }

        return engId;
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

    /// <summary>
    /// Test-only replacement for IGateEvaluator. Returns Satisfied or Blocked on demand
    /// so the readiness and closure paths can be exercised without the Documents service.
    /// </summary>
    private sealed class StubGateEvaluator : IGateEvaluator
    {
        private readonly bool _satisfied;
        public StubGateEvaluator(bool satisfied) => _satisfied = satisfied;

        public Task<GateEvaluationResult> EvaluateAsync(
            Guid engagementId, string tenantId, EngagementStage targetStage, CancellationToken ct = default)
            => Task.FromResult(_satisfied
                ? GateEvaluationResult.Satisfied(Array.Empty<GateRequirementResult>())
                : GateEvaluationResult.Blocked(
                    "Test stub: gate blocked.",
                    new[] { new GateRequirementResult("TestBlocker", false, "Test stub: gate blocked.") }));

        public Task<GateEvaluationResult> EvaluateAsync(
            Guid engagementId, string tenantId, EngagementStage targetStage,
            IReadOnlyList<DocumentSummaryDto> documents,
            IReadOnlyList<EngagementCondition> activeConditions,
            CancellationToken ct = default)
            => EvaluateAsync(engagementId, tenantId, targetStage, ct);
    }
}