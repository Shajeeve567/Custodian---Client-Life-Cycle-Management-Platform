using System.Security.Claims;
using Custodian.Audit.Controllers;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.Reports;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit.Reports;

/// <summary>
/// CSTD-249: the proof-of-delivery endpoint. Who may run it, filter validation, the Staff assignment
/// check against Workflow, and the downloaded PDF.
/// </summary>
public class ProofOfDeliveryReportsControllerTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EngagementId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly Mock<IProofOfDeliveryReportService> _service = new();
    private readonly Mock<IReportRenderer> _renderer = new();
    private readonly Mock<IEngagementAccessClient> _access = new();

    public ProofOfDeliveryReportsControllerTests()
    {
        _renderer.Setup(r => r.RenderPdf(It.IsAny<ReportModel>())).Returns([7, 7, 7]);
        _service.Setup(s => s.BuildReportAsync(Tenant, EngagementId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Report());
    }

    private static ProofOfDeliveryReport Report(bool empty = false)
    {
        var metadata = new ReportMetadata(ProofOfDeliveryReportBuilder.ReportCode, "Proof of Delivery", Tenant.ToString(),
            DateTimeOffset.UtcNow, "owner@example.com", [], "Audit service");
        ReportSection section = empty ? new EmptySection("Timeline") : new TextSection("Timeline", ["Event"]);
        return new ProofOfDeliveryReport(metadata, [section], new TableSection("Detail", [new ReportColumn("Event")], []));
    }

    private ReportsController Controller(bool withAccessClient = true, params Claim[] claims) =>
        new(_service.Object, _renderer.Object, new ReportTelemetry(NullLogger<ReportTelemetry>.Instance), withAccessClient ? _access.Object : null)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        };

    private static Claim[] Caller(string role, string? tenant = null) =>
        [new Claim("tenant_id", tenant ?? Tenant.ToString()), new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.Email, $"{role.ToLowerInvariant()}@example.com")];

    private static ProofOfDeliveryReportQuery Query(string? engagementId = null, string? format = null) =>
        new() { EngagementId = engagementId ?? EngagementId.ToString(), Format = format };

    private static async Task<(HttpResponse Response, byte[] Body)> Download(IActionResult result)
    {
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;
        await result.ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        return (http.Response, body.ToArray());
    }

    [Fact]
    public async Task Owner_GetsThePdf_WithoutAnAssignmentCheck()
    {
        var result = await Controller(claims: Caller("Owner")).ProofOfDelivery(Query(), CancellationToken.None);

        var (response, body) = await Download(result);
        Assert.Equal("application/pdf", response.ContentType);
        Assert.Equal([7, 7, 7], body);
        _service.Verify(s => s.BuildReportAsync(Tenant, EngagementId, "owner@example.com", It.IsAny<CancellationToken>()), Times.Once);
        _access.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmptyReport_IsStillDownloaded()
    {
        _service.Setup(s => s.BuildReportAsync(Tenant, EngagementId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Report(empty: true));

        var (response, _) = await Download(await Controller(claims: Caller("Owner")).ProofOfDelivery(Query(format: "pdf"), CancellationToken.None));

        Assert.Equal("application/pdf", response.ContentType);
    }

    [Fact]
    public async Task Staff_AssignedToTheEngagement_GetsThePdf()
    {
        _access.Setup(a => a.CanAccessEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Controller(claims: Caller("Staff")).ProofOfDelivery(Query(), CancellationToken.None);

        Assert.Equal("application/pdf", (await Download(result)).Response.ContentType);
        _service.Verify(s => s.BuildReportAsync(Tenant, EngagementId, "staff@example.com", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Staff_NotAssigned_IsNotFound()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Controller(claims: Caller("Staff")).ProofOfDelivery(Query(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.SubjectNotFound, error.Kind);
        Assert.Equal($"Engagement '{EngagementId}' was not found in the tenant.", error.Message);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Staff_WhenWorkflowIsUnreachable_IsDataSourceUnavailable()
    {
        _access.Setup(a => a.CanAccessEngagementAsync(EngagementId, Tenant, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new EngagementAccessUnavailableException("down"));

        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Controller(claims: Caller("Staff")).ProofOfDelivery(Query(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, error.Kind);
        Assert.Equal("Workflow service is unreachable. Unable to verify engagement access.", error.Message);
        Assert.IsType<EngagementAccessUnavailableException>(error.InnerException);
    }

    [Fact]
    public async Task StaffWhoIsAlsoOwner_IsNotRestricted()
    {
        var claims = Caller("Staff").Append(new Claim(ClaimTypes.Role, "Owner")).ToArray();

        await Controller(claims: claims).ProofOfDelivery(Query(), CancellationToken.None);

        _access.VerifyNoOtherCalls();
        _service.Verify(s => s.BuildReportAsync(Tenant, EngagementId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Staff_WithoutAnAccessClient_IsNotChecked()
    {
        await Controller(withAccessClient: false, claims: Caller("Staff")).ProofOfDelivery(Query(), CancellationToken.None);

        _service.Verify(s => s.BuildReportAsync(Tenant, EngagementId, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Client_IsForbidden()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Controller(claims: Caller("Client")).ProofOfDelivery(Query(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, error.Kind);
        _service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task TenantClaimThatIsNotAWorkspaceId_IsForbidden(string tenant)
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Controller(claims: Caller("Owner", tenant)).ProofOfDelivery(Query(), CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, error.Kind);
        Assert.Equal("Sign in to a valid workspace to run reports.", error.Message);
    }

    [Theory]
    [InlineData("", "Engagement ID is required.")]
    [InlineData("   ", "Engagement ID is required.")]
    [InlineData("abc", "Engagement ID must be a valid non-empty GUID.")]
    [InlineData("00000000-0000-0000-0000-000000000000", "Engagement ID must be a valid non-empty GUID.")]
    public async Task InvalidEngagementId_IsAnInvalidFilter(string engagementId, string message)
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Controller(claims: Caller("Owner")).ProofOfDelivery(Query(engagementId), CancellationToken.None));

        Assert.Equal(ReportErrorKind.InvalidFilter, error.Kind);
        Assert.Equal("engagementId", error.Field);
        Assert.Equal(message, error.Message);
        _service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CsvFormat_IsAnInvalidFilter()
    {
        var error = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            Controller(claims: Caller("Owner")).ProofOfDelivery(Query(format: "csv"), CancellationToken.None));

        Assert.Equal(ReportErrorKind.InvalidFilter, error.Kind);
        Assert.Equal("format", error.Field);
        Assert.Equal("Proof-of-delivery report is only available in PDF format.", error.Message);
    }
}
