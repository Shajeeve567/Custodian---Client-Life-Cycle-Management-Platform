using System.Security.Claims;
using Custodian.Documents.Controllers;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Custodian.Shared.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Custodian.Documents.Tests.Unit.Reports;

/// <summary>
/// CSTD-182: Tests for ReportsController and ValidationVerificationReportQuery parsing.
/// Verifies query parsing, filter validation errors, authorization enforcement, and file output.
/// </summary>
public class ReportsControllerTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static (ReportsController Controller, Mock<IValidationVerificationReportService> ReportServiceMock, Mock<IReportEngagementScopeResolver> ScopeResolverMock)
        CreateController(string role, string? tenantId = "tenant-001")
    {
        var reportServiceMock = new Mock<IValidationVerificationReportService>(MockBehavior.Strict);
        var scopeResolverMock = new Mock<IReportEngagementScopeResolver>(MockBehavior.Strict);
        var renderer = new PdfReportRenderer();
        var csv = new CsvExporter();
        var telemetry = new ReportTelemetry(NullLogger<ReportTelemetry>.Instance);
        var timeProvider = new FixedTimeProvider(FixedNow);

        var controller = new ReportsController(
            reportServiceMock.Object,
            scopeResolverMock.Object,
            renderer,
            csv,
            telemetry,
            timeProvider);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-456"),
            new(ClaimTypes.Email, $"{role.ToLowerInvariant()}@example.com"),
            new(ClaimTypes.Role, role)
        };

        if (tenantId != null)
        {
            claims.Add(new(TenantContext.ClaimName, tenantId));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = user };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, reportServiceMock, scopeResolverMock);
    }

    [Fact]
    public void QueryParsing_ValidParameters_ProducesExpectedFilter()
    {
        var engId = Guid.NewGuid();
        var query = new ValidationVerificationReportQuery
        {
            From = "2026-09-01",
            To = "2026-09-15",
            EngagementId = engId.ToString(),
            Format = "pdf"
        };

        var filter = query.ToFilter();

        Assert.Equal(new DateOnly(2026, 9, 1), filter.From);
        Assert.Equal(new DateOnly(2026, 9, 15), filter.To);
        Assert.Equal(engId, filter.EngagementId);
    }

    [Theory]
    [InlineData("2026-09-20", "2026-09-10", "from")]
    [InlineData("invalid-date", "2026-09-10", "from")]
    [InlineData("2026-09-01", "invalid-date", "to")]
    [InlineData("01-09-2026", "10-09-2026", "from")]
    public void QueryParsing_InvalidDates_ThrowsInvalidFilterWithField(string from, string to, string expectedField)
    {
        var query = new ValidationVerificationReportQuery
        {
            From = from,
            To = to
        };

        var ex = Assert.Throws<ReportGenerationException>(() => query.ToFilter());
        Assert.Equal(ReportErrorKind.InvalidFilter, ex.Kind);
        Assert.Equal(expectedField, ex.Field);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void QueryParsing_InvalidEngagementId_ThrowsInvalidFilterWithField(string rawId)
    {
        var query = new ValidationVerificationReportQuery
        {
            EngagementId = rawId
        };

        var ex = Assert.Throws<ReportGenerationException>(() => query.ToFilter());
        Assert.Equal(ReportErrorKind.InvalidFilter, ex.Kind);
        Assert.Equal("engagementId", ex.Field);
    }

    [Fact]
    public async Task Owner_GetsPdfReport_WithUnrestrictedTenantScope()
    {
        var (controller, reportMock, scopeMock) = CreateController(nameof(Role.Owner));

        scopeMock.Setup(s => s.ResolveAllowedEngagementIdsAsync(It.IsAny<ClaimsPrincipal>(), "tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<Guid>?)null);

        var data = ValidationVerificationData.Empty("tenant-001");
        reportMock.Setup(r => r.ComputeAggregateAsync("tenant-001", It.IsAny<ValidationVerificationFilter>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(data);

        var query = new ValidationVerificationReportQuery { Format = "pdf" };
        var result = await controller.ValidationVerification(query, CancellationToken.None);

        Assert.NotNull(result);
        scopeMock.Verify(s => s.ResolveAllowedEngagementIdsAsync(It.IsAny<ClaimsPrincipal>(), "tenant-001", It.IsAny<CancellationToken>()), Times.Once);
        reportMock.Verify(r => r.ComputeAggregateAsync("tenant-001", It.IsAny<ValidationVerificationFilter>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Staff_GetsCsvReport_WithAssignedEngagementScope()
    {
        var (controller, reportMock, scopeMock) = CreateController(nameof(Role.Staff));
        var assignedEng = Guid.NewGuid();

        scopeMock.Setup(s => s.ResolveAllowedEngagementIdsAsync(It.IsAny<ClaimsPrincipal>(), "tenant-001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Guid> { assignedEng });

        var data = ValidationVerificationData.Empty("tenant-001");
        reportMock.Setup(r => r.ComputeAggregateAsync(
                "tenant-001",
                It.IsAny<ValidationVerificationFilter>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(assignedEng)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(data);

        var query = new ValidationVerificationReportQuery { Format = "csv" };
        var result = await controller.ValidationVerification(query, CancellationToken.None);

        Assert.NotNull(result);
        scopeMock.Verify(s => s.ResolveAllowedEngagementIdsAsync(It.IsAny<ClaimsPrincipal>(), "tenant-001", It.IsAny<CancellationToken>()), Times.Once);
        reportMock.Verify(r => r.ComputeAggregateAsync("tenant-001", It.IsAny<ValidationVerificationFilter>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClientCaller_IsRejectedWithForbidden()
    {
        var (controller, _, _) = CreateController(nameof(Role.Client));

        var query = new ValidationVerificationReportQuery();
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            controller.ValidationVerification(query, CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task CallerWithoutTenant_IsRejectedWithForbidden()
    {
        var (controller, _, _) = CreateController(nameof(Role.Owner), tenantId: null);

        var query = new ValidationVerificationReportQuery();
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            controller.ValidationVerification(query, CancellationToken.None));

        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
    }

    [Fact]
    public async Task InvalidFormat_ThrowsInvalidFilter()
    {
        var (controller, _, _) = CreateController(nameof(Role.Owner));

        var query = new ValidationVerificationReportQuery { Format = "xlsx" };
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            controller.ValidationVerification(query, CancellationToken.None));

        Assert.Equal(ReportErrorKind.InvalidFilter, ex.Kind);
        Assert.Equal("format", ex.Field);
    }

    [Fact]
    public async Task Staff_ScopeResolverDependencyFailure_PropagatesFailClosed()
    {
        var (controller, _, scopeMock) = CreateController(nameof(Role.Staff));

        scopeMock.Setup(s => s.ResolveAllowedEngagementIdsAsync(It.IsAny<ClaimsPrincipal>(), "tenant-001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(ReportGenerationException.DataSourceUnavailable("The Workflow service is unreachable right now."));

        var query = new ValidationVerificationReportQuery();
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            controller.ValidationVerification(query, CancellationToken.None));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
    }

    [Theory]
    [InlineData(ReportErrorKind.InvalidFilter, 400, "Invalid report filter")]
    [InlineData(ReportErrorKind.Forbidden, 403, "Forbidden")]
    [InlineData(ReportErrorKind.DataSourceUnavailable, 503, "Report data source unavailable — try again")]
    public void ReportExceptionFilter_MapsReportExceptionsToProblemDetails(ReportErrorKind kind, int expectedStatus, string expectedTitle)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/reports/validation-verification";
        var actionContext = new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var exception = new ReportGenerationException(kind, "Test failure message", field: "testField");
        var context = new Microsoft.AspNetCore.Mvc.Filters.ExceptionContext(actionContext, new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>())
        {
            Exception = exception
        };

        var filter = new ReportExceptionFilter(ValidationVerificationReportBuilder.ReportCode, NullLogger<ReportExceptionFilter>.Instance);
        filter.OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);

        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(expectedStatus, problem.Status);
        Assert.Equal(expectedTitle, problem.Title);
        Assert.Equal("VALIDATION_VERIFICATION", problem.Extensions["reportCode"]?.ToString());
    }
}
