using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using UglyToad.PdfPig;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// CSTD-78: the engagement summary endpoints through the real Workflow pipeline (JWT, tenant policy,
/// roles, ReportErrors, portal dashboard and next-action engine) with an in-memory database, a fixed
/// clock and a fake Documents client.
///
/// Now = 2026-10-12 12:00 UTC. Tenant A: E1 (client-1, staff-1, Started, Document Collection) with a
/// pending KYC task, a completed task, an internal-only task, a payment condition with an internal note
/// and one verified document; E2 (client-2). Tenant B: E3 for the same client id "client-1".
/// </summary>
public class EngagementSummaryReportEndpointTests : IClassFixture<EngagementSummaryReportEndpointTests.Factory>
{
    public static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    public static readonly string TenantA = Guid.NewGuid().ToString();
    public static readonly string TenantB = Guid.NewGuid().ToString();
    public static readonly Guid E1 = Guid.Parse("e1e1e1e1-1111-1111-1111-111111111111");
    public static readonly Guid E2 = Guid.NewGuid();
    public static readonly Guid E3 = Guid.NewGuid();

    private const string InternalNote = "INTERNAL-NOTE-DO-NOT-SHOW";
    private const string InternalTaskTitle = "Internal risk review";

    /// <summary>Stands in for the Documents service; <see cref="Down"/> simulates an outage.</summary>
    public sealed class FakeDocuments : IDocumentComplianceClient
    {
        public volatile bool Down;

        public Task<IReadOnlyList<DocumentSummaryDto>> GetDocumentsAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
        {
            if (Down) throw new DocumentComplianceUnavailableException("Documents service returned 503.");
            IReadOnlyList<DocumentSummaryDto> docs = engagementId == E1
                ? [new DocumentSummaryDto
                {
                    DocumentId = Guid.NewGuid(), Type = "Passport", ComplianceStatus = "Compliant", VerificationStatus = "Verified",
                    FileName = "passport.pdf", UploadedAt = new DateTime(2026, 10, 3, 9, 30, 0)
                }]
                : [];
            return Task.FromResult(docs);
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"engagement-summary-endpoint-{Guid.NewGuid()}";
        public FakeDocuments Documents { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:AzureMySqlConnection", "YOUR_SECRET_STRING");
            builder.UseSetting("ConnectionStrings:Default", "");
            builder.UseSetting("Audit:Transport", "Http");
            builder.UseSetting("Kafka:ConsumerEnabled", "false");
            builder.UseSetting("Sla:DefaultOverdueHours", "72");
            builder.ConfigureTestServices(services =>
            {
                services.AddDbContext<WorkflowDbContext>(o => o.UseInMemoryDatabase(_databaseName));
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                services.AddScoped<IDocumentComplianceClient>(_ => Documents);
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            if (db.Engagements.Any()) return;

            db.Engagements.AddRange(
                Engagement(E1, TenantA, "client-1", "staff-1"),
                Engagement(E2, TenantA, "client-2", "staff-2"),
                Engagement(E3, TenantB, "client-1", "staff-1"));
            db.ClientActions.AddRange(
                Action("Provide passport", ClientActionType.KycDocument, ClientActionStatus.Pending, deadline: Oct(15)),
                Action("Confirm contact details", ClientActionType.CustomTask, ClientActionStatus.Completed, completed: Oct(4)),
                Action(InternalTaskTitle, ClientActionType.CustomTask, ClientActionStatus.Pending, internalOnly: true));
            db.EngagementConditions.Add(new EngagementCondition
            {
                EngagementId = E1, TenantId = TenantA, Type = ConditionType.Payment, Title = "Onboarding fee",
                Status = ConditionStatus.Pending, Amount = 1500m, Currency = "LKR", PaymentType = ConditionPaymentType.Upfront,
                DueDateUtc = Oct(20), InternalNote = InternalNote, CreatedBy = "staff-1", CreatedAt = Oct(1)
            });
            db.SaveChanges();
        }

        private static DateTime Oct(int day) => new(2026, 10, day, 0, 0, 0, DateTimeKind.Utc);

        private static Engagement Engagement(Guid id, string tenant, string client, string staff) => new()
        {
            EngagementId = id, TenantId = tenant, ClientId = client, StaffId = staff,
            Status = EngagementStatus.Started, Stage = EngagementStage.DocumentCollection, CreatedAt = Oct(1)
        };

        private static ClientAction Action(string title, string type, string status,
            DateTime? deadline = null, DateTime? completed = null, bool internalOnly = false) => new()
        {
            EngagementId = E1, TenantId = TenantA, Title = title, Type = type, Status = status, StageNumber = 2,
            ActivatedAt = Oct(2), DeadlineUtc = deadline, CompletedAt = completed, IsInternalOnly = internalOnly,
            AssignedToRole = internalOnly ? "Staff" : "Client", Source = "Staff", CreatedAt = Oct(1), UpdatedAt = completed ?? Oct(1)
        };
    }

    // Must match the Jwt section in Workflow's appsettings.json (same values as TestTokenFactory).
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";

    private static string PortalUrl(Guid id) => $"/api/portal/engagements/{id}/summary-report";
    private static string StaffUrl(Guid id) => $"/api/reports/engagement-summary/{id}";

    private readonly Factory _factory;

    public EngagementSummaryReportEndpointTests(Factory factory)
    {
        _factory = factory;
        _factory.Documents.Down = false;
    }

    private HttpClient Client(string role, string tenant, string userId, string? clientIdClaim = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Email, $"{userId}@example.com"),
            new(ClaimTypes.Role, role),
            new("tenant_id", tenant)
        };
        if (clientIdClaim is not null) claims.Add(new Claim("client_id", clientIdClaim));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("custodian-identity", "custodian-services", claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private HttpClient ClientOf(string clientId, string tenant) => Client("Client", tenant, clientId, clientIdClaim: clientId);

    private static async Task<string> PdfText(HttpResponseMessage response)
    {
        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
        return Compact(string.Concat(pdf.GetPages().Select(p => p.Text)));
    }

    private static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public async Task Client_DownloadsOwnReport_AsAPdfAttachment_WithLiveClientSafeContent()
    {
        var response = await ClientOf("client-1", TenantA).GetAsync(PortalUrl(E1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("custodian-engagement_summary-eng-e1e1e1-20261012-1200Z.pdf", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.True(response.Headers.CacheControl!.NoStore);

        var text = await PdfText(response);
        Assert.Contains(Compact("Engagement Summary"), text);
        Assert.Contains("ENG-E1E1E1", text);
        Assert.Contains(Compact("Prepared for client-1@example.com"), text);
        Assert.Contains(Compact("Provide passport"), text);          // outstanding
        Assert.Contains(Compact("Confirm contact details"), text);   // recently completed
        Assert.Contains(Compact("Onboarding fee"), text);            // payment condition
        Assert.Contains("1500", text);
        Assert.Contains("passport.pdf", text);                       // from Documents
        Assert.Contains("2026-10-03T09:30:00Z", text);               // Unspecified kind printed as UTC

        // Client visibility (AC3)
        Assert.DoesNotContain(TenantA, text);
        Assert.DoesNotContain(Compact(InternalNote), text);
        Assert.DoesNotContain(Compact(InternalTaskTitle), text);
        Assert.DoesNotContain("staff-1", text);
        Assert.DoesNotContain(Compact("Staff details"), text);
    }

    [Fact]
    public async Task Client_WithoutClientIdClaim_FallsBackToTheirUserId()
    {
        var response = await Client("Client", TenantA, "client-1").GetAsync(PortalUrl(E1));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string, string> DeniedClients => new()
    {
        { "another client, same tenant", "client-2", "A" },
        { "same client id, other tenant", "client-1", "B" }
    };

    [Theory]
    [MemberData(nameof(DeniedClients))]
    public async Task OtherClientOrTenant_IsNotFound(string _, string clientId, string tenant)
    {
        var response = await ClientOf(clientId, tenant == "A" ? TenantA : TenantB).GetAsync(PortalUrl(E1));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ENGAGEMENT_SUMMARY", problem.RootElement.GetProperty("reportCode").GetString());
    }

    [Fact]
    public async Task ClientIdInQueryOrHeader_IsIgnored()
    {
        var client = ClientOf("client-2", TenantA);
        client.DefaultRequestHeaders.Add("X-Client-ID", "client-1");
        client.DefaultRequestHeaders.Add("X-Tenant-ID", TenantA);

        var response = await client.GetAsync(PortalUrl(E1) + "?clientId=client-1&tenantId=" + TenantA);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_Is401()
    {
        var response = await _factory.CreateClient().GetAsync(PortalUrl(E1));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-00000000000g")]
    public async Task MalformedId_IsNotFound_NeverAServerError(string id)
    {
        var response = await ClientOf("client-1", TenantA).GetAsync($"/api/portal/engagements/{id}/summary-report");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownEngagement_IsNotFound()
    {
        var response = await ClientOf("client-1", TenantA).GetAsync(PortalUrl(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DocumentsDown_Is503_WithAClearMessage_AndNoPdf()
    {
        _factory.Documents.Down = true;

        var response = await ClientOf("client-1", TenantA).GetAsync(PortalUrl(E1));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Report data source unavailable — try again", problem.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("Owner", "owner-1")]
    [InlineData("Staff", "staff-1")]
    public async Task OwnerOrResponsibleStaff_GetTheStaffReport(string role, string userId)
    {
        var response = await Client(role, TenantA, userId).GetAsync(StaffUrl(E1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        var text = await PdfText(response);
        Assert.Contains(Compact("Staff details"), text);
        Assert.Contains("client-1", text);
        Assert.DoesNotContain(Compact(InternalNote), text); // staff route is still the client-safe report
    }

    [Fact]
    public async Task StaffNotAssigned_IsNotFound()
    {
        var response = await Client("Staff", TenantA, "staff-2").GetAsync(StaffUrl(E1));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Owner_OtherTenantsEngagement_IsNotFound()
    {
        var response = await Client("Owner", TenantA, "owner-1").GetAsync(StaffUrl(E3));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Client_OnStaffRoute_IsForbidden()
    {
        var response = await ClientOf("client-1", TenantA).GetAsync(StaffUrl(E1));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Staff_OnClientRoute_IsForbidden()
    {
        var response = await Client("Owner", TenantA, "owner-1").GetAsync(PortalUrl(E1));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
