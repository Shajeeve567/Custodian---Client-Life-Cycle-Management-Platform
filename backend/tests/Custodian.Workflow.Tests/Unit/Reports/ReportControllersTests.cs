using System.Security.Claims;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Custodian.Workflow.Controllers;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Custodian.Workflow.Tests.Unit.ControllerTestHelpers;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>
/// The Workflow report endpoints (ReportsController and EngagementSummaryReportController): who may run
/// them, what is passed to the report services, and the downloaded file.
/// </summary>
public class ReportControllersTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 30, 0, TimeSpan.Zero);
    private static readonly Guid EngagementId = Guid.Parse("abcdef12-0000-0000-0000-000000000001");

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Mock<ISlaPerformanceReportService> _sla = new();
    private readonly Mock<IReadinessHandoffReportService> _readiness = new();
    private readonly Mock<IEngagementSummaryReportService> _summary = new();
    private readonly Mock<IReportRenderer> _renderer = new();
    private readonly Mock<ICsvExporter> _csv = new();

    public ReportControllersTests()
    {
        _renderer.Setup(r => r.RenderPdf(It.IsAny<Custodian.Shared.Reporting.Models.ReportModel>())).Returns([1, 2, 3]);
        _csv.Setup(c => c.ToCsv(It.IsAny<Custodian.Shared.Reporting.Models.TableSection>())).Returns([9, 9]);
    }

    private static Claim[] StaffClaims => [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Staff"), new Claim(ClaimTypes.NameIdentifier, "staff-1"), new Claim(ClaimTypes.Email, "staff@example.com")];
    private static Claim[] OwnerClaims => [new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Owner"), new Claim(ClaimTypes.NameIdentifier, "owner-1")];

    private ReportsController Reports(params Claim[] claims) =>
        new(_sla.Object, _readiness.Object, _renderer.Object, _csv.Object,
            new ReportTelemetry(NullLogger<ReportTelemetry>.Instance), new FixedTimeProvider())
        {
            ControllerContext = ContextFor(claims)
        };

    private EngagementSummaryReportController Summary(params Claim[] claims) =>
        new(_summary.Object, _renderer.Object, new ReportTelemetry(NullLogger<ReportTelemetry>.Instance), new FixedTimeProvider())
        {
            ControllerContext = ContextFor(claims)
        };

    private static async Task<(HttpResponse Response, byte[] Body)> Download(IActionResult result)
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        return (http.Response, body.ToArray());
    }

    private static ReadinessHandoffReportData HandoffData() => new(
        Now,
        new ReadinessHandoffEngagement(EngagementId, Tenant, "client-1", "staff-1", "Closure", "Started", Now.UtcDateTime, null),
        new ReadinessResponse { IsReady = true, State = "Ready" }, false, [], false, [], [], null, null);

    // ==========================================
    // SLA performance
    // ==========================================

    [Fact]
    public async Task SlaPerformance_Pdf_RendersTheReport_ForTheJwtTenant()
    {
        _sla.Setup(s => s.ComputeAsync(Tenant, It.IsAny<SlaReportFilter>(), Now, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SlaPerformanceReportBuilderTests.FourActions());

        var result = await Reports(OwnerClaims).SlaPerformance(new SlaReportQuery(), CancellationToken.None);

        var (response, body) = await Download(result);
        Assert.Equal("application/pdf", response.ContentType);
        Assert.Equal([1, 2, 3], body);
        Assert.Contains("sla_performance", response.Headers.ContentDisposition.ToString());
        _csv.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SlaPerformance_Csv_ExportsTheDetailTable()
    {
        _sla.Setup(s => s.ComputeAsync(Tenant, It.IsAny<SlaReportFilter>(), Now, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SlaPerformanceReportBuilderTests.FourActions());

        var result = await Reports(OwnerClaims).SlaPerformance(new SlaReportQuery { Format = "csv" }, CancellationToken.None);

        var (response, body) = await Download(result);
        Assert.StartsWith("text/csv", response.ContentType);
        Assert.Equal([9, 9], body);
        _renderer.Verify(r => r.RenderPdf(It.IsAny<Custodian.Shared.Reporting.Models.ReportModel>()), Times.Never);
    }

    [Fact]
    public async Task SlaPerformance_Staff_IsRestrictedToTheirEngagements()
    {
        _sla.Setup(s => s.ComputeAsync(Tenant, It.IsAny<SlaReportFilter>(), Now, "staff-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SlaPerformanceReportBuilderTests.FourActions());

        await Reports(StaffClaims).SlaPerformance(new SlaReportQuery(), CancellationToken.None);

        _sla.Verify(s => s.ComputeAsync(Tenant, It.IsAny<SlaReportFilter>(), Now, "staff-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SlaPerformance_Client_IsForbidden()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Reports(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client")).SlaPerformance(new SlaReportQuery(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, error.Kind);
        _sla.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SlaPerformance_NoTenant_IsForbidden()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Reports(new Claim(ClaimTypes.Role, "Owner")).SlaPerformance(new SlaReportQuery(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, error.Kind);
    }

    // ==========================================
    // Delivery readiness & handoff
    // ==========================================

    [Fact]
    public async Task ReadinessHandoff_RendersAPdf_ForTheEngagement()
    {
        _readiness.Setup(s => s.ComputeAsync(Tenant, EngagementId, Now, null, It.IsAny<CancellationToken>())).ReturnsAsync(HandoffData());

        var result = await Reports(OwnerClaims).DeliveryReadinessHandoff(EngagementId.ToString(), CancellationToken.None);

        var (response, body) = await Download(result);
        Assert.Equal("application/pdf", response.ContentType);
        Assert.Equal([1, 2, 3], body);
        Assert.Contains("delivery_readiness_handoff", response.Headers.ContentDisposition.ToString());
        _renderer.Verify(r => r.RenderPdf(It.Is<ReadinessHandoffReport>(m => m.Metadata.GeneratedBy == "owner-1")), Times.Once);
    }

    [Fact]
    public async Task ReadinessHandoff_Staff_IsRestrictedToTheirEngagements()
    {
        _readiness.Setup(s => s.ComputeAsync(Tenant, EngagementId, Now, "staff-1", It.IsAny<CancellationToken>())).ReturnsAsync(HandoffData());

        await Reports(StaffClaims).DeliveryReadinessHandoff(EngagementId.ToString(), CancellationToken.None);

        _readiness.Verify(s => s.ComputeAsync(Tenant, EngagementId, Now, "staff-1", It.IsAny<CancellationToken>()), Times.Once);
        _renderer.Verify(r => r.RenderPdf(It.Is<ReadinessHandoffReport>(m => m.Metadata.GeneratedBy == "staff@example.com")), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task ReadinessHandoff_InvalidEngagementId_IsInvalidFilter(string? engagementId)
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Reports(OwnerClaims).DeliveryReadinessHandoff(engagementId, CancellationToken.None));

        Assert.Equal(ReportErrorKind.InvalidFilter, error.Kind);
        Assert.Equal("engagementId", error.Field);
        Assert.Equal("'engagementId' is required and must be a valid engagement id.", error.Message);
        _readiness.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReadinessHandoff_ClientOrNoTenant_IsForbidden()
    {
        var client = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Reports(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client")).DeliveryReadinessHandoff(EngagementId.ToString(), CancellationToken.None));
        var noTenant = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Reports(new Claim(ClaimTypes.Role, "Owner")).DeliveryReadinessHandoff(EngagementId.ToString(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, client.Kind);
        Assert.Equal(ReportErrorKind.Forbidden, noTenant.Kind);
        _readiness.VerifyNoOtherCalls();
    }

    // ==========================================
    // Engagement summary
    // ==========================================

    private EngagementSummaryRequest? _summaryRequest;

    private void SummaryReturns(string scenario) =>
        _summary.Setup(s => s.ComputeAsync(It.IsAny<EngagementSummaryRequest>(), It.IsAny<CancellationToken>()))
            .Callback((EngagementSummaryRequest r, CancellationToken _) => _summaryRequest = r)
            .ReturnsAsync((EngagementSummaryRequest r, CancellationToken _) =>
                EngagementSummaryAggregator.Aggregate(EngagementSummaryAggregatorTests.Scenario(scenario) with { Audience = r.Audience }));

    [Fact]
    public async Task ClientSummary_UsesTheClientIdentityFromTheJwt()
    {
        SummaryReturns("in-progress");

        var result = await Summary(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), new Claim("client_id", " client-1 "), new Claim(ClaimTypes.Email, "c@example.com"))
            .ClientSummary(EngagementId, CancellationToken.None);

        var request = Assert.IsType<EngagementSummaryRequest>(_summaryRequest);
        Assert.Equal(EngagementId, request.EngagementId);
        Assert.Equal(Tenant, request.TenantId);
        Assert.Equal(EngagementSummaryAudience.Client, request.Audience);
        Assert.Equal("client-1", request.ClientId);
        Assert.Null(request.RestrictedStaffId);
        Assert.Equal("c@example.com", request.PreparedFor);
        Assert.Equal(Now, request.Now);

        var (response, body) = await Download(result);
        Assert.Equal("application/pdf", response.ContentType);
        Assert.Equal([1, 2, 3], body);
        Assert.Contains("engagement_summary", response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task ClientSummary_WithoutClientIdentity_IsForbidden()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Summary(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client")).ClientSummary(EngagementId, CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, error.Kind);
        Assert.Equal("Your sign-in has no client identity.", error.Message);
        _summary.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StaffSummary_Staff_IsRestricted_OwnerIsNot()
    {
        SummaryReturns("empty");

        await Summary(StaffClaims).StaffSummary(EngagementId, CancellationToken.None);
        Assert.Equal(EngagementSummaryAudience.Staff, _summaryRequest!.Audience);
        Assert.Null(_summaryRequest.ClientId);
        Assert.Equal("staff-1", _summaryRequest.RestrictedStaffId);

        await Summary(OwnerClaims).StaffSummary(EngagementId, CancellationToken.None);
        Assert.Null(_summaryRequest!.RestrictedStaffId);
        Assert.Equal("owner-1", _summaryRequest.PreparedFor);
    }

    [Fact]
    public async Task StaffSummary_Client_IsForbidden()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Summary(new Claim("tenant_id", Tenant), new Claim(ClaimTypes.Role, "Client"), new Claim("client_id", "c")).StaffSummary(EngagementId, CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, error.Kind);
        _summary.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("client_id")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public void CallerClientId_ReadsTheFirstIdentityClaim_Trimmed(string claimType)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(claimType, " client-1 ")], "test"));

        Assert.Equal("client-1", EngagementSummaryReportController.CallerClientId(user));
    }

    [Fact]
    public void CallerClientId_PrefersClientId_ThenNameIdentifier()
    {
        var all = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "s"), new Claim(ClaimTypes.NameIdentifier, "n"), new Claim("client_id", "c")], "test"));
        var noClientId = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "s"), new Claim(ClaimTypes.NameIdentifier, "n")], "test"));

        Assert.Equal("c", EngagementSummaryReportController.CallerClientId(all));
        Assert.Equal("n", EngagementSummaryReportController.CallerClientId(noClientId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void CallerClientId_MissingOrBlank_IsNull(string? value)
    {
        var claims = value is null ? Array.Empty<Claim>() : [new Claim("client_id", value)];

        Assert.Null(EngagementSummaryReportController.CallerClientId(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
    }
}
