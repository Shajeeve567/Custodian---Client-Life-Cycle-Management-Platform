using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Custodian.Audit.Data;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.HashChain;
using Custodian.Audit.Services.Reports;
using Custodian.Shared.Reporting.Errors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using UglyToad.PdfPig;
using Xunit;

namespace Custodian.Audit.Tests.Integration;

public class ProofOfDeliveryReportEndpointTests : IClassFixture<ProofOfDeliveryReportEndpointTests.ReportApiFactory>
{
    public sealed class ReportApiFactory : WebApplicationFactory<Program>
    {
        public readonly string DatabaseName = $"audit-pod-tests-{Guid.NewGuid():N}";
        public Mock<IEngagementAccessClient> EngagementAccessMock { get; } = new(MockBehavior.Strict);
        public DateTime FixedNow { get; } = new(2026, 10, 11, 14, 30, 0, DateTimeKind.Utc);

        private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new DateTimeOffset(utcNow, TimeSpan.Zero);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureTestServices(services =>
            {
                // 1. Replace DbContext with InMemoryDatabase
                var toRemove = services.Where(d =>
                    d.ServiceType == typeof(AuditDbContext) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(DbContextOptions<AuditDbContext>) ||
                    (d.ServiceType.IsGenericType && d.ServiceType.GetGenericArguments().Contains(typeof(AuditDbContext)))
                ).ToList();
                foreach (var d in toRemove) services.Remove(d);

                services.AddDbContext<AuditDbContext>(options =>
                    options.UseInMemoryDatabase(DatabaseName));

                // 2. Replace IEngagementAccessClient with mock
                foreach (var d in services.Where(d => d.ServiceType == typeof(IEngagementAccessClient)).ToList())
                {
                    services.Remove(d);
                }
                services.AddScoped<IEngagementAccessClient>(_ => EngagementAccessMock.Object);

                // 3. TimeProvider
                foreach (var d in services.Where(d => d.ServiceType == typeof(TimeProvider)).ToList())
                {
                    services.Remove(d);
                }
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(FixedNow));
            });
        }
    }

    private readonly ReportApiFactory _factory;
    private readonly HashChainService _hashChainService = new();

    public ProofOfDeliveryReportEndpointTests(ReportApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientWithToken(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private WorkflowEngagementSummary CreateSampleEngagementSummary(Guid engagementId, Guid tenantId, string staffId = "staff-1", string status = "InProgress", string stage = "Execution")
    {
        return new WorkflowEngagementSummary
        {
            EngagementId = engagementId,
            TenantId = tenantId.ToString(),
            ClientId = "client-alpha-99",
            StaffId = staffId,
            Status = status,
            Stage = stage,
            StageProgressPercentage = 75,
            CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            ClosedAt = null
        };
    }

    private async Task SeedEventsAsync(params AuditEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await db.Events.AddRangeAsync(events);
        await db.SaveChangesAsync();
    }

    private string ComputeHash(AuditEvent e) =>
        _hashChainService.ComputeEventHash(new EventHashInput
        {
            EventId = e.EventId,
            EngagementId = e.EngagementId,
            TenantId = e.TenantId,
            Actor = e.Actor,
            Type = e.Type,
            Timestamp = e.Timestamp,
            Payload = e.Payload,
            PreviousHash = e.PreviousHash ?? ""
        });

    private (AuditEvent e1, AuditEvent e2, AuditEvent e3) CreateThreeEventChain(Guid tenantId, Guid engagementId)
    {
        var e1Timestamp = _factory.FixedNow.AddDays(-2);
        var e1 = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "system",
            Type = "Genesis",
            Timestamp = e1Timestamp,
            Payload = "{\"action\":\"genesis\"}",
            SequenceNumber = 101,
            PreviousHash = _hashChainService.GenesisHash,
            Hash = ""
        };
        e1.Hash = ComputeHash(e1);

        var e2Timestamp = _factory.FixedNow.AddDays(-1);
        var e2 = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "staff-1",
            Type = "StageChange",
            Timestamp = e2Timestamp,
            Payload = "{\"newStage\":\"Execution\"}",
            SequenceNumber = 102,
            PreviousHash = e1.Hash,
            Hash = ""
        };
        e2.Hash = ComputeHash(e2);

        var e3Timestamp = _factory.FixedNow;
        var e3 = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "staff-1",
            Type = "RequirementSubmitted",
            Timestamp = e3Timestamp,
            Payload = "{\"reqId\":\"req-123\"}",
            SequenceNumber = 103,
            PreviousHash = e2.Hash,
            Hash = ""
        };
        e3.Hash = ComputeHash(e3);

        return (e1, e2, e3);
    }

    // 1. Authorized Owner PDF download
    [Fact]
    public async Task ProofOfDelivery_AuthorizedOwner_ReturnsPdfFileWithHeaders()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
        Assert.Contains($"custodian-proof_of_delivery-", response.Content.Headers.ContentDisposition?.FileName);

        var pdfBytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(pdfBytes);
    }

    // 2. Authorized assigned Staff PDF download
    [Fact]
    public async Task ProofOfDelivery_AuthorizedAssignedStaff_ReturnsPdfFile()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var staffId = "staff-bob";
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId, staffId: staffId);
        _factory.EngagementAccessMock
            .Setup(c => c.CanAccessEngagementAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateStaffToken(tenantId.ToString(), userId: staffId);
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    private static void AssertFilterProblem(ProblemDetails? problem, string expectedField)
    {
        Assert.NotNull(problem);
        Assert.Equal("Invalid report filter", problem.Title);
        Assert.True(problem.Extensions.TryGetValue("field", out var fieldObj));
        Assert.Equal(expectedField, fieldObj?.ToString());
    }

    // 3. Unassigned Staff denied access
    [Fact]
    public async Task ProofOfDelivery_UnassignedStaff_ReturnsNotFoundProblemDetails()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var staffId = "staff-unassigned";

        _factory.EngagementAccessMock
            .Setup(c => c.CanAccessEngagementAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var token = TestTokenFactory.CreateStaffToken(tenantId.ToString(), userId: staffId);
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(404, problem.Status);
        Assert.Equal("Report subject not found", problem.Title);
    }

    // 4. Client, unauthenticated, and global-token access denied
    [Fact]
    public async Task ProofOfDelivery_ClientToken_ReturnsForbidden()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();

        var token = TestTokenFactory.CreateClientToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        // Role check rejects Client caller
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ProofOfDelivery_Unauthenticated_ReturnsUnauthorized()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={Guid.NewGuid()}&format=pdf");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProofOfDelivery_GlobalTokenWithoutTenant_ReturnsForbidden()
    {
        var token = TestTokenFactory.CreateGlobalTokenWithoutTenant(role: "Owner");
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={Guid.NewGuid()}&format=pdf");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // 5. Cross-tenant engagement access denied
    [Fact]
    public async Task ProofOfDelivery_CrossTenantEngagement_ReturnsNotFound()
    {
        var callerTenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();

        // Foreign engagement in Workflow answers null or mismatch for caller's tenant
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, callerTenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowEngagementSummary?)null);

        var token = TestTokenFactory.CreateOwnerToken(callerTenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Report subject not found", problem.Title);
    }

    // 6. Missing, invalid and nonexistent engagement IDs
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ProofOfDelivery_MissingEngagementId_ReturnsBadRequest(string engagementId)
    {
        var tenantId = Guid.NewGuid();
        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        AssertFilterProblem(problem, "engagementId");
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ProofOfDelivery_InvalidEngagementId_ReturnsBadRequest(string invalidId)
    {
        var tenantId = Guid.NewGuid();
        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={invalidId}&format=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        AssertFilterProblem(problem, "engagementId");
    }

    [Fact]
    public async Task ProofOfDelivery_NonexistentEngagement_ReturnsNotFound()
    {
        var tenantId = Guid.NewGuid();
        var nonexistentId = Guid.NewGuid();

        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(nonexistentId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowEngagementSummary?)null);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={nonexistentId}&format=pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Report subject not found", problem.Title);
    }

    [Fact]
    public async Task ProofOfDelivery_UnsupportedFormat_ReturnsBadRequest()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=csv");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        AssertFilterProblem(problem, "format");
    }

    private static string Compact(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    // 7. Valid chain PDF generation and extractable PDF text
    [Fact]
    public async Task ProofOfDelivery_ValidChain_ProducesExtractablePdfTextWithExpectedInformation()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        var compactText = Compact(allText);

        // Header and Title
        Assert.Contains("ProofofDelivery", compactText);
        Assert.Contains(Compact(tenantId.ToString()), compactText);

        // Verification Status
        Assert.Contains("IntegritystatusVALID", compactText);
        Assert.Contains("101-103", compactText);
        Assert.Contains("consistentwithlivedatabasehead", compactText);

        // Timeline content
        Assert.Contains("Genesis", compactText);
        Assert.Contains("StageChange", compactText);
        Assert.Contains("RequirementSubmitted", compactText);

        // Abbreviated hashes
        Assert.Contains($"{e3.Hash![..8]}...", compactText);
    }

    // 8. Tampered-chain status displayed truthfully
    [Fact]
    public async Task ProofOfDelivery_TamperedChain_StatusDisplayedTruthfullyInPdf()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);

        // Tamper e2's payload so hash recomputation fails
        e2.Payload = "{\"tampered\": true}";
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        var compactText = Compact(allText);

        Assert.Contains("IntegritystatusTAMPERED", compactText);
        Assert.Contains("Integrityfailurenotice", compactText);
        Assert.DoesNotContain("IntegritystatusVALID", compactText);
    }

    // 9. Unavailable verification not labelled valid
    [Fact]
    public async Task ProofOfDelivery_UpstreamWorkflowFails_ReturnsServiceUnavailable()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();

        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EngagementAccessUnavailableException("Workflow network timeout"));

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Report data source unavailable — try again", problem.Title);
    }

    // 10. Empty history represented without inventing evidence
    [Fact]
    public async Task ProofOfDelivery_EmptyHistory_PresentsEmptyNoticeWithoutInventedEvents()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));

        Assert.Contains("EMPTY", allText);
        Assert.Contains("No audit events have been recorded for this engagement", allText);
    }

    // 11. Generation timestamp and verified boundary displayed correctly
    [Fact]
    public async Task ProofOfDelivery_MetadataContainsExactTimestampAndBoundary()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));

        // FixedNow: 2026-10-11 14:30
        Assert.Contains("2026-10-11 14:30", allText);
        Assert.Contains("101 - 103", allText);
    }

    // 12. Existing report endpoints remain unaffected
    [Fact]
    public async Task ExistingHealthEndpoint_RemainsUnaffected()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("Audit Service", content);
    }

    // 13. Unavailable hash-verification produces PDF with notice and never reports valid
    [Fact]
    public async Task ProofOfDelivery_VerificationUnavailable_ProducesPdfWithNoticeAndNeverValid()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var mockHashService = new Mock<IHashChainService>();
                mockHashService.Setup(h => h.GenesisHash).Returns(HashChainService.Genesis);
                mockHashService.Setup(h => h.ComputeEventHash(It.IsAny<EventHashInput>())).Returns("dummy");
                mockHashService.Setup(h => h.VerifyEventHash(It.IsAny<EventHashInput>(), It.IsAny<string>()))
                    .Throws(new InvalidOperationException("Cryptographic subsystem fault"));

                foreach (var d in services.Where(d => d.ServiceType == typeof(IHashChainService)).ToList())
                {
                    services.Remove(d);
                }
                services.AddSingleton<IHashChainService>(mockHashService.Object);
            });
        });

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        var compactText = Compact(allText);

        Assert.Contains("IntegritystatusUNAVAILABLE", compactText);
        Assert.Contains("Verificationnotice", compactText);
        Assert.Contains("Cryptographicsubsystemfault", compactText);
        Assert.DoesNotContain("IntegritystatusVALID", compactText);
    }

    // 14. Live-head mismatch produces historical snapshot notice instead of live-head verified
    [Fact]
    public async Task ProofOfDelivery_LiveHeadMismatch_ProducesHistoricalSnapshotNoticeNotLiveHeadVerified()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var divergentHead = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "staff-new",
            Type = "NewEventAppendedAfterSnapshot",
            Timestamp = _factory.FixedNow.AddMinutes(5),
            Payload = "{}",
            SequenceNumber = 104,
            PreviousHash = e3.Hash,
            Hash = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        };

        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var mockRepo = new Mock<IAuditEventRepository>();
                mockRepo.Setup(r => r.GetByEngagementIdInChainOrderAsync(tenantId, engagementId))
                    .ReturnsAsync(new List<AuditEvent> { e1, e2, e3 });
                mockRepo.Setup(r => r.GetLatestForEngagementAsync(tenantId, engagementId))
                    .ReturnsAsync(divergentHead);

                foreach (var d in services.Where(d => d.ServiceType == typeof(IAuditEventRepository)).ToList())
                {
                    services.Remove(d);
                }
                services.AddScoped<IAuditEventRepository>(_ => mockRepo.Object);
            });
        });

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = customFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        var compactText = Compact(allText);

        Assert.Contains("Historicalsnapshotonly", compactText);
        Assert.Contains("liveheaddiffers/unverified", compactText);
        Assert.Contains("Liveheadconsistencynotice", compactText);
        Assert.DoesNotContain("consistentwithlivedatabasehead", compactText);
    }

    // 15. Deterministic fixture: persisted hashes and abbreviated excerpts match stored evidence
    [Fact]
    public async Task ProofOfDelivery_DeterministicFixture_PersistedHashesAndAbbreviatedExcerptsMatchExactly()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        var summary = CreateSampleEngagementSummary(engagementId, tenantId);
        _factory.EngagementAccessMock
            .Setup(c => c.GetEngagementSummaryAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        var response = await client.GetAsync($"/api/reports/proof-of-delivery?engagementId={engagementId}&format=pdf");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pdfBytes = await response.Content.ReadAsByteArrayAsync();

        using var pdf = PdfDocument.Open(pdfBytes);
        var allText = string.Join("\n", pdf.GetPages().Select(p => p.Text));
        var compactText = Compact(allText);

        // Exact sequence numbers match
        Assert.Contains("101", compactText);
        Assert.Contains("102", compactText);
        Assert.Contains("103", compactText);
        Assert.Contains("101-103", compactText);

        // Abbreviated IDs match
        Assert.Contains(Compact(ProofOfDeliveryReportBuilder.AbbreviateId(e1.EventId)), compactText);
        Assert.Contains(Compact(ProofOfDeliveryReportBuilder.AbbreviateId(e2.EventId)), compactText);
        Assert.Contains(Compact(ProofOfDeliveryReportBuilder.AbbreviateId(e3.EventId)), compactText);

        // Chain tip hash abbreviation matches e3.Hash
        var expectedTipAbbr = $"{e3.Hash![..8]}...";
        Assert.Contains(expectedTipAbbr, compactText);

        // Timeline excerpts match stored hashes
        var expectedE1Abbr = $"{e1.Hash![..8]}...";
        var expectedE2Abbr = $"{e2.Hash![..8]}...";
        Assert.Contains(expectedE1Abbr, compactText);
        Assert.Contains(expectedE2Abbr, compactText);

        // Service level detail verification: full 64-char hashes preserved in data model
        using var scope = _factory.Services.CreateScope();
        var reportService = scope.ServiceProvider.GetRequiredService<IProofOfDeliveryReportService>();
        var report = await reportService.BuildReportAsync(tenantId, engagementId, "auditor-test");
        Assert.NotNull(report.Detail);
        Assert.Equal(3, report.Detail.Rows.Count);
        Assert.Equal(e1.Hash, report.Detail.Rows[0][6]);
        Assert.Equal(e2.Hash, report.Detail.Rows[1][6]);
        Assert.Equal(e3.Hash, report.Detail.Rows[2][6]);
        Assert.Equal(e1.EventId.ToString("D"), report.Detail.Rows[0][1]);
        Assert.Equal(e2.EventId.ToString("D"), report.Detail.Rows[1][1]);
        Assert.Equal(e3.EventId.ToString("D"), report.Detail.Rows[2][1]);
    }

    // 16. Existing Audit endpoints and security policies remain functional
    [Fact]
    public async Task ExistingAuditEndpoints_And_Security_RemainFullyFunctional()
    {
        var tenantId = Guid.NewGuid();
        var engagementId = Guid.NewGuid();
        var (e1, e2, e3) = CreateThreeEventChain(tenantId, engagementId);
        await SeedEventsAsync(e1, e2, e3);

        _factory.EngagementAccessMock
            .Setup(c => c.CanAccessEngagementAsync(engagementId, tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var token = TestTokenFactory.CreateOwnerToken(tenantId.ToString());
        var client = CreateClientWithToken(token);

        // 1. Audit events query endpoint works
        var eventsResponse = await client.GetAsync($"/api/audit-events/engagement/{engagementId}");
        Assert.Equal(HttpStatusCode.OK, eventsResponse.StatusCode);
        var eventsList = await eventsResponse.Content.ReadFromJsonAsync<List<AuditEventResponse>>();
        Assert.NotNull(eventsList);
        Assert.Equal(3, eventsList.Count);

        // 2. Audit chain verify endpoint works
        var verifyResponse = await client.GetAsync($"/api/audit-events/verify?engagementId={engagementId}");
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verifyResult = await verifyResponse.Content.ReadFromJsonAsync<ChainVerificationResult>();
        Assert.NotNull(verifyResult);
        Assert.True(verifyResult.IsVerified);
        Assert.Equal(3, verifyResult.Count);

        // 3. Audit ingest endpoint fails without valid API key (security check)
        var anonymousClient = _factory.CreateClient();
        var validCreateBody = new CreateAuditEventRequest
        {
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "unauthorized-client",
            Type = "TestEvent",
            Payload = "{}"
        };
        var ingestResponse = await anonymousClient.PostAsJsonAsync("/api/audit-events", validCreateBody);
        Assert.Equal(HttpStatusCode.Forbidden, ingestResponse.StatusCode);
    }
}
