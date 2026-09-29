using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// Staff only reach engagements they are responsible for (Engagement.StaffId); Owners reach the whole
/// workspace; clients are unaffected. Runs through the real Workflow pipeline, so the global
/// StaffEngagementAccessFilter and the narrowed list endpoints are both exercised.
///
/// Workspace T: "Assigned" (responsible: staff A, client-a) and "Unassigned" (responsible: the owner,
/// client-b). Each has one overdue client task (activated 10 days ago, 72 h SLA).
/// </summary>
public class StaffEngagementAccessTests : IClassFixture<StaffEngagementAccessTests.Factory>
{
    public static readonly string Tenant = Guid.NewGuid().ToString();
    public static readonly string StaffA = Guid.NewGuid().ToString();
    public static readonly string OwnerId = Guid.NewGuid().ToString();
    public static readonly Guid Assigned = Guid.NewGuid();
    public static readonly Guid Unassigned = Guid.NewGuid();

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"staff-access-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:AzureMySqlConnection", "YOUR_SECRET_STRING");
            builder.UseSetting("ConnectionStrings:Default", "");
            builder.UseSetting("Audit:Transport", "Http");
            builder.UseSetting("Kafka:ConsumerEnabled", "false");
            builder.ConfigureTestServices(services =>
                services.AddDbContext<WorkflowDbContext>(o => o.UseInMemoryDatabase(_databaseName)));
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            if (db.Engagements.Any()) return;

            var activated = DateTime.UtcNow.AddDays(-10);
            db.Engagements.AddRange(
                new Engagement { EngagementId = Assigned, TenantId = Tenant, ClientId = "client-a", StaffId = StaffA, Status = EngagementStatus.Started, CreatedAt = activated },
                new Engagement { EngagementId = Unassigned, TenantId = Tenant, ClientId = "client-b", StaffId = OwnerId, Status = EngagementStatus.Started, CreatedAt = activated });
            db.ClientActions.AddRange(
                new ClientAction { EngagementId = Assigned, TenantId = Tenant, Title = "Assigned task", StageNumber = 1, ActivatedAt = activated, CreatedAt = activated, UpdatedAt = activated },
                new ClientAction { EngagementId = Unassigned, TenantId = Tenant, Title = "Unassigned task", StageNumber = 1, ActivatedAt = activated, CreatedAt = activated, UpdatedAt = activated });
            db.SaveChanges();
        }
    }

    // Must match the Jwt section in Workflow's appsettings.json (same values as TestTokenFactory).
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";

    private readonly Factory _factory;

    public StaffEngagementAccessTests(Factory factory)
    {
        _factory = factory;
    }

    private HttpClient As(string role, string userId, string? clientId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Email, $"{role.ToLowerInvariant()}@example.com"),
            new(ClaimTypes.Role, role),
            new("tenant_id", Tenant)
        };
        if (clientId != null) claims.Add(new Claim("client_id", clientId));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("custodian-identity", "custodian-services", claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private HttpClient Staff() => As("Staff", StaffA);
    private HttpClient Owner() => As("Owner", OwnerId);

    private static async Task<List<Guid>> EngagementIds(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.EnumerateArray().Select(e => e.GetProperty("engagementId").GetGuid()).ToList();
    }

    // ---------------- Lists ----------------

    [Fact]
    public async Task EngagementList_Staff_SeesOnlyAssigned_OwnerSeesAll()
    {
        // Other tests in this class add engagements for staff A, so check membership, not the exact list.
        var staffIds = await EngagementIds(await Staff().GetAsync("/api/Engagements"));
        Assert.Contains(Assigned, staffIds);
        Assert.DoesNotContain(Unassigned, staffIds);

        var ownerIds = await EngagementIds(await Owner().GetAsync("/api/Engagements"));
        Assert.Contains(Assigned, ownerIds);
        Assert.Contains(Unassigned, ownerIds);
    }

    // The stall queue reads through raw MySQL SQL (not runnable on the in-memory database), so its Staff
    // restriction is checked at the controller in StallQueueControllerAccessTests.

    [Fact]
    public async Task SlaReport_Staff_CoversOnlyAssigned()
    {
        var csv = await (await Staff().GetAsync("/api/reports/sla-performance?format=csv")).Content.ReadAsStringAsync();
        Assert.Contains("Assigned task", csv);
        Assert.DoesNotContain("Unassigned task", csv);

        var other = await Staff().GetAsync($"/api/reports/sla-performance?engagementId={Unassigned}");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        var ownerCsv = await (await Owner().GetAsync("/api/reports/sla-performance?format=csv")).Content.ReadAsStringAsync();
        Assert.Contains("Unassigned task", ownerCsv);
    }

    // ---------------- Engagement-scoped reads ----------------

    public static readonly TheoryData<string> ScopedReads = new()
    {
        "/api/Engagements/{0}",
        "/api/Engagements/{0}/next-action",
        "/api/engagements/{0}/actions",
        "/api/engagements/{0}/actions/standard-checklist/preview",
        "/api/engagements/{0}/requirements",
        "/api/engagements/{0}/conditions",
        "/api/engagements/{0}/interventions",
        "/api/engagements/{0}/stall",
        "/api/portal/engagements/{0}"
    };

    [Theory]
    [MemberData(nameof(ScopedReads))]
    public async Task UnassignedEngagement_IsNotFound_ForStaff(string route)
    {
        var response = await Staff().GetAsync(string.Format(route, Unassigned));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/Engagements/{0}")]
    [InlineData("/api/engagements/{0}/actions")]
    [InlineData("/api/engagements/{0}/requirements")]
    [InlineData("/api/engagements/{0}/conditions")]
    [InlineData("/api/engagements/{0}/interventions")]
    public async Task AssignedEngagement_IsOpen_ForStaff_AndEveryEngagementForOwners(string route)
    {
        Assert.Equal(HttpStatusCode.OK, (await Staff().GetAsync(string.Format(route, Assigned))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Owner().GetAsync(string.Format(route, Unassigned))).StatusCode);
    }

    // ---------------- Engagement-scoped writes ----------------

    [Fact]
    public async Task UnassignedEngagement_CannotBeChangedByStaff()
    {
        var staff = Staff();
        var tenant = new { tenantId = Tenant };

        var createTask = await staff.PostAsJsonAsync($"/api/engagements/{Unassigned}/actions",
            new { title = "Sneaky task", type = "CustomTask", source = "Staff", stageNumber = 1 });
        var status = await staff.PutAsJsonAsync($"/api/Engagements/{Unassigned}/status", new { tenantId = Tenant, status = "Closed" });
        var recheck = await staff.PostAsync($"/api/Engagements/{Unassigned}/recheck", null);
        var intervention = await staff.PostAsJsonAsync($"/api/engagements/{Unassigned}/interventions",
            new { type = "RecoveryAction", reason = "x", outcome = "NoChange" });
        var delete = await staff.DeleteAsync($"/api/Engagements/{Unassigned}");

        Assert.All([createTask, status, recheck, intervention, delete], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));

        // Nothing changed.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
        Assert.False(db.ClientActions.Any(a => a.Title == "Sneaky task"));
        Assert.Equal(EngagementStatus.Started, db.Engagements.AsNoTracking().Single(e => e.EngagementId == Unassigned).Status);
        Assert.False(db.Interventions.Any(i => i.EngagementId == Unassigned));
    }

    [Fact]
    public async Task StaffCreatingAnEngagement_IsMadeResponsibleForIt()
    {
        var response = await Staff().PostAsJsonAsync("/api/Engagements",
            new { tenantId = Tenant, clientId = "client-new", staffId = OwnerId });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(StaffA, json.RootElement.GetProperty("staffId").GetString());
    }

    // ---------------- Reassignment (owner only) ----------------

    [Fact]
    public async Task OnlyOwners_CanChangeTheResponsibleStaff()
    {
        var response = await Staff().PutAsJsonAsync($"/api/Engagements/{Assigned}/staff", new { staffId = OwnerId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Reassigning_GivesTheNewStaffMemberAccess()
    {
        // Its own engagement, so the other tests' data is untouched.
        var engagementId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            db.Engagements.Add(new Engagement { EngagementId = engagementId, TenantId = Tenant, ClientId = "client-c", StaffId = OwnerId, Status = EngagementStatus.Started });
            db.SaveChanges();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Staff().GetAsync($"/api/Engagements/{engagementId}")).StatusCode);

        var response = await Owner().PutAsJsonAsync($"/api/Engagements/{engagementId}/staff", new { staffId = StaffA });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Staff().GetAsync($"/api/Engagements/{engagementId}")).StatusCode);
        Assert.Contains(engagementId, await EngagementIds(await Staff().GetAsync("/api/Engagements")));
    }

    // ---------------- Portal preview and clients ----------------

    [Fact]
    public async Task PortalPreview_Staff_OnlyForAssignedClients()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Staff().GetAsync("/api/portal/my-engagement?clientId=client-b")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Staff().GetAsync("/api/portal/my-engagement?clientId=client-a")).StatusCode);
    }

    [Fact]
    public async Task Clients_AreUnaffected()
    {
        var client = As("Client", "client-b", clientId: "client-b");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/Engagements/{Unassigned}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/Engagements/{Assigned}")).StatusCode);
    }
}
