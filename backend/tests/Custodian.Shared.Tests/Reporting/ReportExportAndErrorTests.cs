using System.Security.Claims;
using System.Text;
using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Custodian.Shared.Tests.Reporting;

public class ReportExportAndErrorTests
{
    private static ReportMetadata Metadata() => new(
        "SLA_PERFORMANCE",
        "SLA Performance",
        "tenant-a",
        new DateTimeOffset(2026, 9, 28, 10, 30, 45, TimeSpan.Zero),
        "owner@example.com",
        [],
        "Workflow service live database");

    // ---------------- Format and file name ----------------

    [Theory]
    [InlineData(null, ReportFormat.Pdf)]
    [InlineData("", ReportFormat.Pdf)]
    [InlineData("pdf", ReportFormat.Pdf)]
    [InlineData("PDF", ReportFormat.Pdf)]
    [InlineData(" csv ", ReportFormat.Csv)]
    public void Format_DefaultsToPdf(string? value, ReportFormat expected)
    {
        Assert.Equal(expected, ReportFormats.Parse(value));
    }

    [Theory]
    [InlineData("xlsx")]
    [InlineData("pdf,csv")]
    public void Format_Unsupported_IsAnInvalidFilterOnFormat(string value)
    {
        var ex = Assert.Throws<ReportGenerationException>(() => ReportFormats.Parse(value));

        Assert.Equal(ReportErrorKind.InvalidFilter, ex.Kind);
        Assert.Equal("format", ex.Field);
    }

    [Fact]
    public void FileName_FollowsTheConvention()
    {
        Assert.Equal("custodian-sla_performance-20260928-1030Z.pdf", ReportResults.FileName(Metadata(), ReportFormat.Pdf));
        Assert.Equal("custodian-sla_performance-20260928-1030Z.csv", ReportResults.FileName(Metadata(), ReportFormat.Csv));
    }

    [Fact]
    public async Task File_SetsDownloadHeaders_AndWritesTheBytes()
    {
        var content = Encoding.ASCII.GetBytes("%PDF-1.7 test");
        var output = ReportResults.Output(Metadata(), ReportFormat.Pdf, content);
        var http = new DefaultHttpContext();
        var body = new MemoryStream();
        http.Response.Body = body;

        await ReportResults.File(output).ExecuteResultAsync(new ActionContext(http, new RouteData(), new ActionDescriptor()));

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("application/pdf", http.Response.ContentType);
        Assert.Equal("attachment; filename=\"custodian-sla_performance-20260928-1030Z.pdf\"", http.Response.Headers.ContentDisposition.ToString());
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Equal(content.Length, http.Response.ContentLength);
        Assert.Equal(content, body.ToArray());
    }

    [Fact]
    public void CsvOutput_IsTextCsvUtf8()
    {
        Assert.Equal("text/csv; charset=utf-8", ReportResults.Output(Metadata(), ReportFormat.Csv, []).ContentType);
    }

    // ---------------- Error contract ----------------

    private static ExceptionContext Fail(Exception exception)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/reports/sla-performance";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ExceptionContext(action, new List<IFilterMetadata>()) { Exception = exception };
        new ReportExceptionFilter("SLA_PERFORMANCE", NullLogger<ReportExceptionFilter>.Instance).OnException(context);
        return context;
    }

    private static ProblemDetails Problem(ExceptionContext context)
    {
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Contains("application/problem+json", result.ContentTypes);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(result.StatusCode, problem.Status);
        return problem;
    }

    [Theory]
    [InlineData(ReportErrorKind.InvalidFilter, 400, "Invalid report filter")]
    [InlineData(ReportErrorKind.Forbidden, 403, "Forbidden")]
    [InlineData(ReportErrorKind.SubjectNotFound, 404, "Report subject not found")]
    [InlineData(ReportErrorKind.DataSourceUnavailable, 503, "Report data source unavailable — try again")]
    [InlineData(ReportErrorKind.GenerationFailed, 500, "Report could not be generated")]
    public void EachKind_MapsToItsStatusAndTitle(ReportErrorKind kind, int status, string title)
    {
        var problem = Problem(Fail(new ReportGenerationException(kind, "Message for the user.")));

        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.Equal("SLA_PERFORMANCE", problem.Extensions["reportCode"]);
        Assert.True(problem.Extensions.ContainsKey("correlationId"));
        Assert.Equal("/api/reports/sla-performance", problem.Instance);
    }

    [Fact]
    public void InvalidFilter_NamesTheField_AndShowsTheMessage()
    {
        var problem = Problem(Fail(ReportGenerationException.InvalidFilter("from", "'from' must be on or before 'to'.")));

        Assert.Equal("from", problem.Extensions["field"]);
        Assert.Equal("'from' must be on or before 'to'.", problem.Detail);
    }

    [Fact]
    public void UnexpectedException_Is500_WithoutLeakingItsMessage()
    {
        var problem = Problem(Fail(new InvalidOperationException("Connection string Server=db;Password=secret")));

        Assert.Equal(500, problem.Status);
        Assert.DoesNotContain("secret", problem.Detail);
        Assert.Contains((string)problem.Extensions["correlationId"]!, problem.Detail);
    }

    [Fact]
    public void ExceptionReportCode_OverridesTheEndpointCode()
    {
        var problem = Problem(Fail(new ReportGenerationException(ReportErrorKind.SubjectNotFound, "Not found.", reportCode: "READINESS")));

        Assert.Equal("READINESS", problem.Extensions["reportCode"]);
    }

    [Fact]
    public void AbortedRequest_IsLeftAlone()
    {
        var http = new DefaultHttpContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        http.RequestAborted = cts.Token;
        var context = new ExceptionContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), new List<IFilterMetadata>())
        {
            Exception = new OperationCanceledException()
        };

        new ReportExceptionFilter("SLA_PERFORMANCE", NullLogger<ReportExceptionFilter>.Instance).OnException(context);

        Assert.False(context.ExceptionHandled);
    }

    // ---------------- Tenant and role ----------------

    private static ClaimsPrincipal User(string? role, string? tenantId = "tenant-a", string? email = null, string? sub = "user-1")
    {
        var claims = new List<Claim>();
        if (role != null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (tenantId != null) claims.Add(new Claim("tenant_id", tenantId));
        if (email != null) claims.Add(new Claim(ClaimTypes.Email, email));
        if (sub != null) claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("Owner")]
    [InlineData("Staff")]
    public void StaffAndOwners_MayRunReports(string role)
    {
        ReportAuthorization.EnsureStaffOrOwner(User(role));
    }

    [Theory]
    [InlineData("Client")]
    [InlineData(null)]
    public void OthersAreForbidden(string? role)
    {
        var ex = Assert.Throws<ReportGenerationException>(() => ReportAuthorization.EnsureStaffOrOwner(User(role)));
        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public void Tenant_ComesFromTheClaim_AndIsRequired()
    {
        Assert.Equal("tenant-a", ReportAuthorization.RequireTenantId(User("Owner", tenantId: " tenant-a ")));

        var ex = Assert.Throws<ReportGenerationException>(() => ReportAuthorization.RequireTenantId(User("Owner", tenantId: null)));
        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public void Actor_PrefersEmail_ThenUserId()
    {
        Assert.Equal("owner@example.com", ReportAuthorization.ResolveActor(User("Owner", email: "owner@example.com")));
        Assert.Equal("user-1", ReportAuthorization.ResolveActor(User("Owner")));
        Assert.Equal("Unknown", ReportAuthorization.ResolveActor(User("Owner", sub: null)));
    }
}
