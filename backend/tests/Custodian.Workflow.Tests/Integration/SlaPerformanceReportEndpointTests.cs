using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Custodian.Workflow.Data;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
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
/// CSTD-37: GET api/reports/sla-performance through the real Workflow pipeline (JWT, tenant policy,
/// roles, ReportErrors), with a fixed clock and an in-memory database.
///
/// Now = 2026-09-28 12:00 UTC, SLA 72 h, default range Aug 30 .. Sep 28. Tenant A, engagement E1:
///   On time: activated Sep10, completed Sep11 (due Sep13).
///   Late:    activated Sep10, deadline Sep11, completed Sep12.
///   Overdue: activated Sep20, pending (due Sep23).
/// → 3 actions, 1 on time, 1 late, 1 overdue, on-time rate 50.0 %. Tenant B has one action of its own.
/// </summary>
public class SlaPerformanceReportEndpointTests : IClassFixture<SlaPerformanceReportEndpointTests.Factory>
{
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    public static readonly string TenantA = Guid.NewGuid().ToString();
    public static readonly string TenantB = Guid.NewGuid().ToString();
    public static readonly Guid E1 = Guid.Parse("a1b2c3d4-1111-1111-1111-111111111111");
    public static readonly Guid E2 = Guid.NewGuid();

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"sla-endpoint-{Guid.NewGuid()}";

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
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            if (db.Engagements.Any()) return;

            db.Engagements.AddRange(Engagement(E1, TenantA), Engagement(E2, TenantB));
            db.ClientActions.AddRange(
                Action(E1, TenantA, "On time task", Sep(10), null, Sep(11)),
                Action(E1, TenantA, "Late task", Sep(10), Sep(11), Sep(12)),
                Action(E1, TenantA, "Overdue task", Sep(20), null, null),
                Action(E2, TenantB, "Tenant B task", Sep(15), null, null));
            db.SaveChanges();
        }

        private static DateTime Sep(int day) => new(2026, 9, day, 0, 0, 0, DateTimeKind.Utc);

        private static Engagement Engagement(Guid id, string tenant) => new()
        {
            EngagementId = id, TenantId = tenant, ClientId = "client-1", StaffId = "staff-1",
            Status = EngagementStatus.Started, Stage = EngagementStage.DocumentCollection, CreatedAt = Sep(1)
        };

        private static ClientAction Action(Guid engagementId, string tenant, string title, DateTime activated, DateTime? deadline, DateTime? completed) => new()
        {
            EngagementId = engagementId, TenantId = tenant, Title = title, Type = ClientActionType.KycDocument,
            Status = completed.HasValue ? ClientActionStatus.Completed : ClientActionStatus.Pending,
            StageNumber = 1, ActivatedAt = activated, DeadlineUtc = deadline, CompletedAt = completed,
            CreatedAt = Sep(1), UpdatedAt = completed ?? Sep(1)
        };
    }

    // Must match the Jwt section in Workflow's appsettings.json (same values as TestTokenFactory).
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string Url = "/api/reports/sla-performance";

    private readonly Factory _factory;

    public SlaPerformanceReportEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string role, string tenant)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, "owner@example.com"),
            new(ClaimTypes.Role, role),
            new("tenant_id", tenant)
        };
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("custodian-identity", "custodian-services", claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Compact(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    [Fact]
    public async Task Pdf_IsTheDefault_AndContainsTheSummaryNumbers()
    {
        var response = await Client("Owner", TenantA).GetAsync(Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("custodian-sla_performance-20260928-1200Z.pdf", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.True(response.Headers.CacheControl!.NoStore);

        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
        var text = Compact(string.Concat(pdf.GetPages().Select(p => p.Text)));
        Assert.Contains("SLAPerformance", text);
        Assert.Contains("Totalactions3", text);
        Assert.Contains("Completedontime1", text);
        Assert.Contains("Completedlate1", text);
        Assert.Contains("Open–overdue1", text);
        Assert.Contains("On-timerate50.0%", text);
        Assert.Contains("From:2026-08-30", text); // default range printed in the filters block
        Assert.Contains("ENG-A1B2C3", text);      // top overdue
        Assert.Contains("Overduetask", text);
        Assert.DoesNotContain("TenantBtask", text);
    }

    [Fact]
    public async Task Csv_HasOneRowPerAction()
    {
        var response = await Client("Staff", TenantA).GetAsync($"{Url}?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".csv", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));

        var lines = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync())
            .TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // header + 3 actions
        Assert.StartsWith("Engagement,Engagement id,Action id,Action,", lines[0]);
        Assert.Contains(lines, l => l.Contains("Late task") && l.Contains("Completed late"));
    }

    [Fact]
    public async Task OtherTenant_SeesOnlyItsOwnActions()
    {
        var response = await Client("Owner", TenantB).GetAsync($"{Url}?format=csv");

        var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("Tenant B task", body);
        Assert.DoesNotContain("Overdue task", body);
    }

    [Fact]
    public async Task Client_IsForbidden()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("Client", TenantA).GetAsync(Url)).StatusCode);
    }

    [Theory]
    [InlineData("stage=9", "stage")]
    [InlineData("from=2026-09-10&to=2026-09-01", "from")]
    [InlineData("from=10-09-2026", "from")]
    [InlineData("actionType=Passport", "actionType")]
    [InlineData("format=xlsx", "format")]
    public async Task BadFilter_Is400ProblemDetails_NamingTheField(string query, string field)
    {
        var response = await Client("Owner", TenantA).GetAsync($"{Url}?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Invalid report filter", problem!.Title);
        Assert.Equal(field, problem.Extensions["field"]!.ToString());
        Assert.Equal("SLA_PERFORMANCE", problem.Extensions["reportCode"]!.ToString());
    }

    [Fact]
    public async Task EngagementOfAnotherTenant_Is404()
    {
        var response = await Client("Owner", TenantA).GetAsync($"{Url}?engagementId={E2}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Report subject not found", problem!.Title);
    }

    [Fact]
    public async Task NoMatches_IsStillAValidPdf()
    {
        var response = await Client("Owner", TenantA).GetAsync($"{Url}?stage=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
        var text = Compact(string.Concat(pdf.GetPages().Select(p => p.Text)));
        Assert.Contains("Totalactions0", text);
        Assert.Contains("Norecordsmatchtheselectedfilters.", text);
    }
}
