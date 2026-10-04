using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Custodian.Documents.Data;
using Custodian.Documents.Models;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Contracts;
using Custodian.Shared.Reporting.Errors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using UglyToad.PdfPig;
using Xunit;

namespace Custodian.Documents.Tests.Integration;

/// <summary>
/// CSTD-31 / CSTD-258: Service/API HTTP integration tests for the Validation &amp; Verification Report endpoint.
/// Exercises the real ASP.NET Core HTTP pipeline (routing, authentication, tenant authorization filters,
/// ReportsController, ValidationVerificationReportService, and PDF/CSV rendering pipeline).
/// </summary>
public class ValidationVerificationReportEndpointTests : IClassFixture<ValidationVerificationReportEndpointTests.Factory>
{
    private const string JwtKey = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string JwtIssuer = "custodian-identity";
    private const string JwtAudience = "custodian-services";

    public sealed class StubScopeResolver : IReportEngagementScopeResolver
    {
        public Func<ClaimsPrincipal, string, Task<IReadOnlyCollection<Guid>?>>? Resolver { get; set; }

        public Task<IReadOnlyCollection<Guid>?> ResolveAllowedEngagementIdsAsync(
            ClaimsPrincipal user,
            string tenantId,
            CancellationToken ct = default)
        {
            // Owner is always unrestricted (null) across their workspace, matching WorkflowReportEngagementScopeResolver
            if (user.IsInRole("Owner"))
            {
                return Task.FromResult<IReadOnlyCollection<Guid>?>(null);
            }

            if (Resolver != null)
            {
                return Resolver(user, tenantId);
            }

            return Task.FromResult<IReadOnlyCollection<Guid>?>(Array.Empty<Guid>());
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"doc-report-integration-{Guid.NewGuid()}";
        public StubScopeResolver ScopeResolverStub { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", "");
            builder.UseSetting("ConnectionStrings:AzureMySqlConnection", "YOUR_SECRET_STRING");
            builder.UseSetting("Audit:Transport", "Http");
            builder.UseSetting("Storage:UploadPath", Path.Combine(Path.GetTempPath(), $"custodian-test-uploads-{Guid.NewGuid():N}"));

            builder.ConfigureTestServices(services =>
            {
                services.AddDbContext<DocumentDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));

                // Replace Workflow engagement scope resolver with our controllable test stub
                services.AddScoped<IReportEngagementScopeResolver>(_ => ScopeResolverStub);
            });
        }
    }

    private readonly Factory _factory;

    public ValidationVerificationReportEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthenticatedClient(string role, string userId, string tenantId)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Email, $"{role.ToLowerInvariant()}@custodian.com"),
            new(ClaimTypes.Role, role),
            new("role", role),
            new("tenant_id", tenantId)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: creds);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenantId);
        return client;
    }

    private async Task SeedDocumentsAsync(IEnumerable<DocumentMetadata> documents)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        db.Documents.AddRange(documents);
        await db.SaveChangesAsync();
    }

    // =========================================================================
    // SCENARIO A: OWNER PDF REPORT
    // =========================================================================

    [Fact]
    public async Task ScenarioA_OwnerPdfReport_ReturnsSuccessWithValidPdfAndSeededData()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var engagementId = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = new DateTime(2026, 10, 10, 10, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                VerificationStatus = DocumentVerificationStatus.Verified,
                UploaderId = "user-1",
                FileName = "passport.pdf"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                VerificationStatus = DocumentVerificationStatus.Rejected,
                VerificationReason = "HUMAN-NAME-MISMATCH",
                UploaderId = "user-2",
                FileName = "utility.pdf"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "BankStatement",
                UploadedAt = new DateTime(2026, 10, 18, 14, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Rejected,
                RejectionReason = "AUTO-EXPIRED",
                UploaderId = "user-3",
                FileName = "bank.pdf"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var ownerClient = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Act
        var response = await ownerClient.GetAsync("/api/reports/validation-verification?format=pdf");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);

        var fileName = disposition.FileNameStar ?? disposition.FileName;
        Assert.False(string.IsNullOrWhiteSpace(fileName));

        var normalizedFileName = fileName.Trim('"');
        Assert.EndsWith(".pdf", normalizedFileName, StringComparison.OrdinalIgnoreCase);

        var pdfBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(pdfBytes);

        // Verify PDF Magic Header (%PDF-)
        var header = Encoding.ASCII.GetString(pdfBytes.AsSpan(0, 5));
        Assert.Equal("%PDF-", header);

        // Extract PDF text and verify seeded values appear in the document
        using var pdfDoc = PdfDocument.Open(pdfBytes);
        var allPdfText = string.Join(" ", pdfDoc.GetPages().Select(p => p.Text));

        Assert.Contains("Validation & Verification", allPdfText);
        Assert.Contains("Total uploads", allPdfText);
        Assert.Contains("Passport", allPdfText);
        Assert.Contains("UtilityBill", allPdfText);
        Assert.Contains("BankStatement", allPdfText);
    }

    // =========================================================================
    // SCENARIO B: OWNER CSV REPORT
    // =========================================================================

    [Fact]
    public async Task ScenarioB_OwnerCsvReport_ReturnsDeterministicTotalsAndBreakdown()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var engagementId = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                VerificationStatus = DocumentVerificationStatus.Verified,
                UploaderId = "u-1"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "BankStatement",
                UploadedAt = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Rejected,
                RejectionReason = "AUTO-BLURRY",
                UploaderId = "u-2"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var ownerClient = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Act
        var response = await ownerClient.GetAsync("/api/reports/validation-verification?format=csv");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var csv = await response.Content.ReadAsStringAsync();
        Assert.Contains("Category,Metric,Count", csv);
        Assert.Contains("Summary,Total uploads,2", csv);
        Assert.Contains("Automatic compliance,Compliant,1", csv);
        Assert.Contains("Automatic compliance,Rejected,1", csv);
        Assert.Contains("Document type,Passport,1", csv);
        Assert.Contains("Document type,BankStatement,1", csv);
        Assert.Contains("Automatic rejection reason,AUTO-BLURRY,1", csv);
    }

    // =========================================================================
    // SCENARIO C: DATE FILTER
    // =========================================================================

    [Fact]
    public async Task ScenarioC_DateFilter_ReturnsOnlyRecordsWithinDateRange()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var engagementId = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), // Before
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-1"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc), // Inside
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-2"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "BankStatement",
                UploadedAt = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc), // After
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-3"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var client = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Filter: 2026-10-10 to 2026-10-20
        var response = await client.GetAsync("/api/reports/validation-verification?format=csv&from=2026-10-10&to=2026-10-20");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,1", csv);
        Assert.Contains("Document type,UtilityBill,1", csv);
        Assert.DoesNotContain("Passport", csv);
        Assert.DoesNotContain("BankStatement", csv);
    }

    // =========================================================================
    // SCENARIO D: ENGAGEMENT FILTER
    // =========================================================================

    [Fact]
    public async Task ScenarioD_EngagementFilter_ReturnsOnlyRequestedEngagementRecords()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var engagementA = Guid.NewGuid();
        var engagementB = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementA,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-1"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementB,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-2"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var client = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Filter: engagementId = engagementA
        var response = await client.GetAsync($"/api/reports/validation-verification?format=csv&engagementId={engagementA}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,1", csv);
        Assert.Contains("Document type,Passport,1", csv);
        Assert.DoesNotContain("UtilityBill", csv);
    }

    // =========================================================================
    // SCENARIO E: EMPTY RESULT
    // =========================================================================

    [Fact]
    public async Task ScenarioE_EmptyResult_ReturnsValidZeroReportWithoutError()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var client = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Request a range with no documents
        var response = await client.GetAsync("/api/reports/validation-verification?format=csv&from=2025-01-01&to=2025-01-10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,0", csv);
        Assert.Contains("Automatic compliance,Compliant,0", csv);
    }

    // =========================================================================
    // SCENARIO F: TENANT ISOLATION
    // =========================================================================

    [Fact]
    public async Task ScenarioF_TenantIsolation_TenantANeverSeesTenantBRecords()
    {
        var tenantA = $"tenant-A-{Guid.NewGuid():N}";
        var tenantB = $"tenant-B-{Guid.NewGuid():N}";

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = Guid.NewGuid(),
                TenantId = tenantA,
                Type = "Passport",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-A"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = Guid.NewGuid(),
                TenantId = tenantB,
                Type = "UtilityBill",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-B"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var clientA = CreateAuthenticatedClient("Owner", $"owner-A-{Guid.NewGuid():N}", tenantA);

        var response = await clientA.GetAsync("/api/reports/validation-verification?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,1", csv);
        Assert.Contains("Document type,Passport,1", csv);
        Assert.DoesNotContain("UtilityBill", csv);
    }

    // =========================================================================
    // SCENARIO G: STAFF ACCESS (ASSIGNED ENGAGEMENTS ONLY)
    // =========================================================================

    [Fact]
    public async Task ScenarioG_StaffAccess_ReturnsOnlyAssignedEngagementData()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var assignedEngagement = Guid.NewGuid();
        var unassignedEngagement = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = assignedEngagement,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-1"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = unassignedEngagement,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-2"
            }
        };
        await SeedDocumentsAsync(fixtures);

        // Configure stub: Staff user is assigned ONLY to assignedEngagement
        _factory.ScopeResolverStub.Resolver = (user, tid) =>
            Task.FromResult<IReadOnlyCollection<Guid>?>(new[] { assignedEngagement });

        var staffClient = CreateAuthenticatedClient("Staff", $"staff-{Guid.NewGuid():N}", tenantId);

        var response = await staffClient.GetAsync("/api/reports/validation-verification?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,1", csv);
        Assert.Contains("Document type,Passport,1", csv);
        Assert.DoesNotContain("UtilityBill", csv);
    }

    // =========================================================================
    // SCENARIO H: STAFF UNASSIGNED ENGAGEMENT (CANNOT EXPAND SCOPE)
    // =========================================================================

    [Fact]
    public async Task ScenarioH_StaffUnassignedEngagement_ReturnsZeroReportWithoutLeak()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var assignedEngagement = Guid.NewGuid();
        var unassignedEngagement = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = unassignedEngagement,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = DateTime.UtcNow,
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-2"
            }
        };
        await SeedDocumentsAsync(fixtures);

        _factory.ScopeResolverStub.Resolver = (user, tid) =>
            Task.FromResult<IReadOnlyCollection<Guid>?>(new[] { assignedEngagement });

        var staffClient = CreateAuthenticatedClient("Staff", $"staff-{Guid.NewGuid():N}", tenantId);

        // Staff tries to query unassignedEngagement explicitly
        var response = await staffClient.GetAsync($"/api/reports/validation-verification?format=csv&engagementId={unassignedEngagement}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        // Must return 0 uploads and NOT leak the unassigned document
        Assert.Contains("Summary,Total uploads,0", csv);
        Assert.DoesNotContain("UtilityBill", csv);
    }

    // =========================================================================
    // SCENARIO I: CLIENT ACCESS (FORBIDDEN)
    // =========================================================================

    [Fact]
    public async Task ScenarioI_ClientAccess_Returns403Forbidden()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var client = CreateAuthenticatedClient("Client", $"client-{Guid.NewGuid():N}", tenantId);

        var response = await client.GetAsync("/api/reports/validation-verification?format=csv");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // =========================================================================
    // SCENARIO J: DEPENDENCY FAILURE (WORKFLOW UNREACHABLE FAILS CLOSED)
    // =========================================================================

    [Fact]
    public async Task ScenarioJ_WorkflowDependencyFailure_FailsClosedWith503ServiceUnavailable()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        // Simulate Workflow outage for Staff caller
        _factory.ScopeResolverStub.Resolver = (user, tid) =>
            throw ReportGenerationException.DataSourceUnavailable("The Workflow service is unreachable right now.");

        var staffClient = CreateAuthenticatedClient("Staff", $"staff-{Guid.NewGuid():N}", tenantId);

        var response = await staffClient.GetAsync("/api/reports/validation-verification?format=csv");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problemJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("data source unavailable", problemJson.ToLowerInvariant());
    }

    // =========================================================================
    // SCENARIO K: FILTER / PDF / CSV PARITY
    // =========================================================================

    [Fact]
    public async Task ScenarioK_FilterPdfCsvParity_PdfAndCsvMatchSameFixtureAggregate()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var engagementId = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                VerificationStatus = DocumentVerificationStatus.Verified,
                UploaderId = "u-1"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Rejected,
                RejectionReason = "BLURRY-IMAGE",
                UploaderId = "u-2"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var client = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Fetch CSV
        var csvResp = await client.GetAsync($"/api/reports/validation-verification?format=csv&engagementId={engagementId}");
        Assert.Equal(HttpStatusCode.OK, csvResp.StatusCode);
        var csv = await csvResp.Content.ReadAsStringAsync();

        // Fetch PDF
        var pdfResp = await client.GetAsync($"/api/reports/validation-verification?format=pdf&engagementId={engagementId}");
        Assert.Equal(HttpStatusCode.OK, pdfResp.StatusCode);
        var pdfBytes = await pdfResp.Content.ReadAsByteArrayAsync();

        using var pdfDoc = PdfDocument.Open(pdfBytes);
        var pdfText = string.Join(" ", pdfDoc.GetPages().Select(p => p.Text));

        // Parity checks:
        // CSV: Total uploads, 2
        Assert.Contains("Summary,Total uploads,2", csv);
        Assert.Contains("Automatic compliance,Compliant,1", csv);
        Assert.Contains("Automatic compliance,Rejected,1", csv);

        // PDF text contains the exact same metrics:
        Assert.Contains("Total uploads", pdfText);
        Assert.Contains("Passport", pdfText);
        Assert.Contains("UtilityBill", pdfText);
        Assert.Contains("BLURRY-IMAGE", pdfText);
    }

    // =========================================================================
    // SCENARIO L: BOUNDARY DATE & STAFF NO ASSIGNMENTS
    // =========================================================================

    [Fact]
    public async Task ScenarioL_BoundaryDate_MatchesExactSingleDate()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var engagementId = Guid.NewGuid();

        var fixtures = new[]
        {
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "Passport",
                UploadedAt = new DateTime(2026, 10, 15, 8, 30, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-1"
            },
            new DocumentMetadata
            {
                DocumentId = Guid.NewGuid(),
                EngagementId = engagementId,
                TenantId = tenantId,
                Type = "UtilityBill",
                UploadedAt = new DateTime(2026, 10, 16, 0, 1, 0, DateTimeKind.Utc),
                ComplianceStatus = Compliance.ComplianceStatus.Compliant,
                UploaderId = "u-2"
            }
        };
        await SeedDocumentsAsync(fixtures);

        var client = CreateAuthenticatedClient("Owner", $"owner-{Guid.NewGuid():N}", tenantId);

        // Exact single date boundary: from=2026-10-15 & to=2026-10-15
        var response = await client.GetAsync("/api/reports/validation-verification?format=csv&from=2026-10-15&to=2026-10-15");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,1", csv);
        Assert.Contains("Document type,Passport,1", csv);
        Assert.DoesNotContain("UtilityBill", csv);
    }

    [Fact]
    public async Task ScenarioM_StaffWithNoAssignedEngagements_ReturnsZeroReport()
    {
        var tenantId = $"tenant-{Guid.NewGuid():N}";

        _factory.ScopeResolverStub.Resolver = (user, tid) =>
            Task.FromResult<IReadOnlyCollection<Guid>?>(Array.Empty<Guid>());

        var staffClient = CreateAuthenticatedClient("Staff", $"staff-{Guid.NewGuid():N}", tenantId);

        var response = await staffClient.GetAsync("/api/reports/validation-verification?format=csv");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Contains("Summary,Total uploads,0", csv);
    }
}
