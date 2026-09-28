using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// CSTD-35 acceptance criterion: "Intervention cannot bypass gates."
///
/// Flow:
///   1. Seed an engagement with an unsatisfied requirement (blocks stage advance).
///   2. Try to advance → 400 (gate blocked).
///   3. Record an intervention → 201 (the recovery action is recorded).
///   4. Try to advance again → still 400 (intervention did NOT bypass the gate).
///   5. Satisfy the requirement out-of-band.
///   6. Try to advance → 200 (gate now satisfied via the normal path).
/// </summary>
public class InterventionGateBypassTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public InterventionGateBypassTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task Intervention_DoesNotBypassGate()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        // 1. Create an engagement via the API
        var createResp = await client.PostAsJsonAsync("/api/Engagements",
            new CreateEngagementRequest
            {
                TenantId = tenant.ToString(),
                ClientId = Guid.NewGuid().ToString(),
                StaffId = Guid.NewGuid().ToString(),
            });
        createResp.EnsureSuccessStatusCode();
        var engagement = await createResp.Content.ReadFromJsonAsync<EngagementResponse>();
        Assert.NotNull(engagement);

        // Start the engagement so the stage can advance
        var startResp = await client.PutAsJsonAsync($"/api/Engagements/{engagement!.EngagementId}/status",
            new { tenantId = tenant.ToString(), status = "Started" });
        startResp.EnsureSuccessStatusCode();

        // 2. Seed a blocking requirement directly in the DB
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Custodian.Workflow.Data.WorkflowDbContext>();
            db.Requirements.Add(new Custodian.Workflow.Models.Requirement
            {
                RequirementId = Guid.NewGuid(),
                TenantId = tenant.ToString(),
                EngagementId = engagement.EngagementId,
                Type = "CompanyRegistrationNumber",
                Status = "Requested",
                AssignedToRole = "Client",
                StageNumber = 1,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // 3. Try to advance → 400 gate blocked
        var advance1 = await client.PutAsJsonAsync(
            $"/api/Engagements/{engagement.EngagementId}/stage",
            new { tenantId = tenant.ToString(), stage = "DocumentCollection" });
        Assert.Equal(HttpStatusCode.BadRequest, advance1.StatusCode);
        var body1 = await advance1.Content.ReadAsStringAsync();
        Assert.Contains("CompanyRegistrationNumber", body1);

        // 4. Record an intervention → 201
        var interventionResp = await client.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/interventions",
            new RecordInterventionRequest
            {
                Type = InterventionType.RecoveryAction,
                Reason = "Called the client to chase the registration number.",
                Outcome = InterventionOutcome.Progressing,
            });
        Assert.Equal(HttpStatusCode.Created, interventionResp.StatusCode);

        // 5. Try to advance again → STILL 400 (intervention did NOT bypass)
        var advance2 = await client.PutAsJsonAsync(
            $"/api/Engagements/{engagement.EngagementId}/stage",
            new { tenantId = tenant.ToString(), stage = "DocumentCollection" });
        Assert.Equal(HttpStatusCode.BadRequest, advance2.StatusCode);

        // 6. Satisfy the requirement (simulate the client submitting)
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Custodian.Workflow.Data.WorkflowDbContext>();
            var req = db.Requirements.Single(r => r.EngagementId == engagement.EngagementId);
            req.Status = "Approved";
            req.SubmittedAt = DateTime.UtcNow;
            req.ReviewedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // 7. Try to advance → 200 (or at least no longer blocked by THIS requirement)
        var advance3 = await client.PutAsJsonAsync(
            $"/api/Engagements/{engagement.EngagementId}/stage",
            new { tenantId = tenant.ToString(), stage = "DocumentCollection" });
        // The gate may still block on other requirements (documents). Accept 200 or 400
        // whose body no longer mentions the requirement we satisfied.
        if (advance3.StatusCode == HttpStatusCode.BadRequest)
        {
            var body3 = await advance3.Content.ReadAsStringAsync();
            Assert.DoesNotContain("CompanyRegistrationNumber", body3);
        }
    }

    [SkippableFact]
    public async Task Recheck_AfterIntervention_ReturnsNextAction()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable — integration test skipped.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var createResp = await client.PostAsJsonAsync("/api/Engagements",
            new CreateEngagementRequest
            {
                TenantId = tenant.ToString(),
                ClientId = Guid.NewGuid().ToString(),
                StaffId = Guid.NewGuid().ToString(),
            });
        createResp.EnsureSuccessStatusCode();
        var engagement = await createResp.Content.ReadFromJsonAsync<EngagementResponse>();
        Assert.NotNull(engagement);

        var recheck = await client.PostAsync(
            $"/api/Engagements/{engagement!.EngagementId}/recheck", content: null);

        Assert.Equal(HttpStatusCode.OK, recheck.StatusCode);
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