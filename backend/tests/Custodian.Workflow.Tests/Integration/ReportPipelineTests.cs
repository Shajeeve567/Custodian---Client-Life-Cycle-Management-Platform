using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Rendering;
using Custodian.Workflow.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// CSTD-36-3: the shared report plumbing inside the real Workflow pipeline (JWT, tenant policy, role
/// attribute, ReportErrors filter, ReportResults). A test-only controller stands in for a real report;
/// the first production report endpoint arrives with CSTD-37.
/// </summary>
public class ReportPipelineTests : IClassFixture<ReportPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:AzureMySqlConnection", "YOUR_SECRET_STRING");
            builder.UseSetting("ConnectionStrings:Default", "");
            builder.UseSetting("Audit:Transport", "Http");
            builder.UseSetting("Kafka:ConsumerEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.AddDbContext<WorkflowDbContext>(o => o.UseInMemoryDatabase($"reports-{Guid.NewGuid()}"));
                services.AddControllers().AddApplicationPart(typeof(TestReportController).Assembly);
            });
        }
    }

    // Must match the Jwt section in Workflow's appsettings.json (same values as TestTokenFactory).
    private const string Key = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";

    private readonly Factory _factory;

    public ReportPipelineTests(Factory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string role, bool withTenant = true)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, "owner@example.com"),
            new(ClaimTypes.Role, role)
        };
        if (withTenant) claims.Add(new Claim("tenant_id", Guid.NewGuid().ToString()));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("custodian-identity", "custodian-services", claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public void Workflow_RegistersTheSharedRendererAndExporter()
    {
        using var scope = _factory.Services.CreateScope();

        Assert.IsType<PdfReportRenderer>(scope.ServiceProvider.GetRequiredService<IReportRenderer>());
        Assert.IsType<CsvExporter>(scope.ServiceProvider.GetRequiredService<ICsvExporter>());
    }

    [Theory]
    [InlineData(null, "application/pdf", ".pdf")]
    [InlineData("csv", "text/csv; charset=utf-8", ".csv")]
    public async Task Staff_DownloadsTheReport(string? format, string contentType, string extension)
    {
        var url = "/api/test-reports/sample" + (format is null ? "" : $"?format={format}");

        var response = await Client("Staff").GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType!.ToString());
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.StartsWith("custodian-sample-", response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.EndsWith(extension, response.Content.Headers.ContentDisposition.FileName!.Trim('"'));
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Client_IsForbidden()
    {
        var response = await Client("Client").GetAsync("/api/test-reports/sample");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TokenWithoutTenant_IsForbidden()
    {
        var response = await Client("Owner", withTenant: false).GetAsync("/api/test-reports/sample");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task UnsupportedFormat_Is400ProblemDetails()
    {
        var response = await Client("Owner").GetAsync("/api/test-reports/sample?format=xlsx");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Invalid report filter", problem!.Title);
        Assert.Equal("SAMPLE", problem.Extensions["reportCode"]!.ToString());
        Assert.Equal("format", problem.Extensions["field"]!.ToString());
    }

    [Fact]
    public async Task UnexpectedFailure_Is500_WithoutDetails()
    {
        var response = await Client("Owner").GetAsync("/api/test-reports/broken");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Report could not be generated", body);
        Assert.DoesNotContain("internal detail", body);
    }
}

/// <summary>Test-only report endpoint; mirrors how a real report controller is written.</summary>
[ApiController]
[Route("api/test-reports")]
[Authorize(Roles = "Owner,Staff")]
[ReportErrors("SAMPLE")]
public class TestReportController : ControllerBase
{
    private sealed class SampleReport(ReportMetadata metadata, IEnumerable<ReportSection> sections)
        : ReportModel(metadata, sections);

    private readonly IReportRenderer _renderer;
    private readonly ICsvExporter _csv;

    public TestReportController(IReportRenderer renderer, ICsvExporter csv)
    {
        _renderer = renderer;
        _csv = csv;
    }

    [HttpGet("sample")]
    public IActionResult Sample([FromQuery] string? format)
    {
        var reportFormat = ReportFormats.Parse(format);
        var tenantId = ReportAuthorization.RequireTenantId(User);
        var metadata = new ReportMetadata("SAMPLE", "Sample", tenantId, DateTimeOffset.UtcNow,
            ReportAuthorization.ResolveActor(User), [], "Workflow service live database");
        var table = new TableSection("Rows", [new ReportColumn("Name")], [new object?[] { "One" }]);
        var model = new SampleReport(metadata, [table]);

        var bytes = reportFormat == ReportFormat.Csv ? _csv.ToCsv(table) : _renderer.RenderPdf(model);
        return ReportResults.File(ReportResults.Output(metadata, reportFormat, bytes));
    }

    [HttpGet("broken")]
    public IActionResult Broken() => throw new InvalidOperationException("internal detail");
}
