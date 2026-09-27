using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// Runs requests through the real Workflow pipeline (authentication, the tenant policy on every
/// controller endpoint, role attributes). The environment is "Testing" and MySQL is not configured,
/// so appsettings.Development.json (which may point at a shared database) is never loaded; an
/// in-memory database stands in, so nothing is written anywhere real.
/// </summary>
public class SecurityHardeningEndpointTests : IClassFixture<SecurityHardeningEndpointTests.NoDatabaseFactory>
{
    public sealed class NoDatabaseFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:AzureMySqlConnection", "YOUR_SECRET_STRING");
            builder.UseSetting("ConnectionStrings:Default", "");
            builder.UseSetting("Audit:Transport", "Http");
            builder.UseSetting("Kafka:ConsumerEnabled", "false");
            builder.ConfigureTestServices(services =>
                services.AddDbContext<WorkflowDbContext>(o => o.UseInMemoryDatabase(DatabaseName)));
        }

        public string DatabaseName { get; } = $"security-{Guid.NewGuid()}";

        public void Seed(params Engagement[] engagements)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            db.Engagements.AddRange(engagements);
            db.SaveChanges();
        }
    }

    // Must match the Jwt section in Workflow's appsettings.json (same values as TestTokenFactory).
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string Issuer = "custodian-identity";
    private const string Audience = "custodian-services";

    private readonly NoDatabaseFactory _factory;
    private readonly Guid _tenantId = Guid.NewGuid();

    public SecurityHardeningEndpointTests(NoDatabaseFactory factory)
    {
        _factory = factory;
    }

    private static string Token(Guid? tenantId, string? role, string? clientId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, clientId ?? Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, "user@example.com")
        };
        if (tenantId.HasValue) claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        if (role != null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (clientId != null) claims.Add(new Claim("client_id", clientId));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Audience, claims, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private HttpClient Client(string? token, Guid? tenantHeader = null)
    {
        var client = _factory.CreateClient();
        if (token != null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (tenantHeader.HasValue)
        {
            client.DefaultRequestHeaders.Add("X-Tenant-ID", tenantHeader.Value.ToString());
        }
        return client;
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        var response = await Client(null).GetAsync("/api/Engagements");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task C1_GlobalLoginToken_WithTenantHeader_CannotReadAnyTenant()
    {
        // Identity's login token: sub + email only. Previously X-Tenant-ID then chose the tenant.
        var client = Client(Token(tenantId: null, role: null), tenantHeader: _tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/Engagements")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/actions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/portal/my-engagement")).StatusCode);
    }

    [Fact]
    public async Task C1_TokenWithRoleButNoTenant_IsRejectedOnRoleRestrictedEndpoints()
    {
        var client = Client(Token(tenantId: null, role: "Owner"), tenantHeader: _tenantId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/next-action")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/stall-queue")).StatusCode);
    }

    [Fact]
    public async Task C2_Client_CannotCreateChangeOrDeleteEngagements()
    {
        var client = Client(Token(_tenantId, "Client", clientId: "client-1"));
        var id = Guid.NewGuid();

        var create = await client.PostAsJsonAsync("/api/Engagements", new { tenantId = _tenantId.ToString(), clientId = "client-1", staffId = "staff-1" });
        var status = await client.PutAsJsonAsync($"/api/Engagements/{id}/status", new { tenantId = _tenantId.ToString(), status = "Closed" });
        var stage = await client.PutAsJsonAsync($"/api/Engagements/{id}/stage", new { tenantId = _tenantId.ToString(), stage = "DocumentCollection" });
        var delete = await client.DeleteAsync($"/api/Engagements/{id}");

        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stage.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
    }

    [Fact]
    public async Task C3_Client_SendingXUserRoleStaff_CannotWriteConditions()
    {
        var client = Client(Token(_tenantId, "Client", clientId: "client-1"));
        client.DefaultRequestHeaders.Add("X-User-Role", "Staff");
        var engagementId = Guid.NewGuid();
        var conditionId = Guid.NewGuid();

        var attach = await client.PostAsJsonAsync($"/api/engagements/{engagementId}/conditions", new { type = "Approval", title = "Scope approval" });
        var update = await client.PatchAsJsonAsync($"/api/engagements/{engagementId}/conditions/{conditionId}", new { title = "x" });
        var deactivate = await client.PutAsJsonAsync($"/api/engagements/{engagementId}/conditions/{conditionId}/deactivate", new { reason = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, attach.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deactivate.StatusCode);
    }

    private Engagement NewEngagement(string clientId) => new()
    {
        EngagementId = Guid.NewGuid(),
        TenantId = _tenantId.ToString(),
        ClientId = clientId,
        StaffId = "staff-1",
        Status = EngagementStatus.Started,
        Stage = EngagementStage.Onboarding,
        CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task StaffWorkspaceToken_SeesWholeTenant()
    {
        var mine = NewEngagement("client-a");
        var other = NewEngagement("client-b");
        _factory.Seed(mine, other);

        var response = await Client(Token(_tenantId, "Staff")).GetAsync("/api/Engagements");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ids = (await response.Content.ReadFromJsonAsync<List<EngagementDto>>())!.Select(e => e.EngagementId).ToList();
        Assert.Contains(mine.EngagementId, ids);
        Assert.Contains(other.EngagementId, ids);
    }

    [Fact]
    public async Task C2_Client_SeesOnlyItsOwnEngagements()
    {
        var mine = NewEngagement("client-a");
        var other = NewEngagement("client-b");
        _factory.Seed(mine, other);
        var client = Client(Token(_tenantId, "Client", clientId: "client-a"));

        var list = await client.GetFromJsonAsync<List<EngagementDto>>("/api/Engagements");
        var ownById = await client.GetAsync($"/api/Engagements/{mine.EngagementId}");
        var otherById = await client.GetAsync($"/api/Engagements/{other.EngagementId}");

        Assert.Contains(list!, e => e.EngagementId == mine.EngagementId);
        Assert.DoesNotContain(list!, e => e.EngagementId == other.EngagementId);
        Assert.Equal(HttpStatusCode.OK, ownById.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherById.StatusCode);
    }

    private sealed record EngagementDto(Guid EngagementId, string ClientId);
}
