using System.Net;
using System.Security.Claims;
using Custodian.Documents.Services.Reports;
using Custodian.Shared.Auth;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Custodian.Documents.Tests.Unit.Reports;

/// <summary>
/// CSTD-182: Tests for WorkflowReportEngagementScopeResolver.
/// Verifies:
/// 8. Owner uses full authenticated tenant scope (returns null without calling Workflow).
/// 9. Staff passes only allowed engagement IDs to aggregate service.
/// 10. Staff scope dependency failure fails closed (throws DataSourceUnavailable on network failure or server error).
/// 11. Client role cannot access report endpoint (throws Forbidden).
/// </summary>
public class WorkflowReportEngagementScopeResolverTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static (WorkflowReportEngagementScopeResolver Resolver, StubHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        string? authHeader = "Bearer staff-jwt-token")
    {
        var handler = new StubHandler(respond);
        var httpContext = new DefaultHttpContext();
        if (authHeader != null)
        {
            httpContext.Request.Headers.Authorization = authHeader;
        }

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://workflow.test") };
        var resolver = new WorkflowReportEngagementScopeResolver(
            client,
            accessor,
            NullLogger<WorkflowReportEngagementScopeResolver>.Instance);

        return (resolver, handler);
    }

    private static ClaimsPrincipal CreateUser(string role, string tenantId = "tenant-001")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-123"),
            new(ClaimTypes.Email, $"{role.ToLowerInvariant()}@example.com"),
            new(ClaimTypes.Role, role),
            new(TenantContext.ClaimName, tenantId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task Owner_ReturnsNullScope_WithoutCallingWorkflow()
    {
        var called = false;
        var (resolver, _) = Create(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var owner = CreateUser(nameof(Role.Owner));
        var scope = await resolver.ResolveAllowedEngagementIdsAsync(owner, "tenant-001");

        Assert.Null(scope);
        Assert.False(called);
    }

    [Fact]
    public async Task Staff_QueriesWorkflow_ForwardsToken_AndReturnsAssignedEngagementIds()
    {
        var eng1 = Guid.NewGuid();
        var eng2 = Guid.NewGuid();
        var json = $"[{{\"engagementId\":\"{eng1}\"}},{{\"engagementId\":\"{eng2}\"}}]";

        var (resolver, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        });

        var staff = CreateUser(nameof(Role.Staff));
        var scope = await resolver.ResolveAllowedEngagementIdsAsync(staff, "tenant-001");

        Assert.NotNull(scope);
        Assert.Equal(2, scope.Count);
        Assert.Contains(eng1, scope);
        Assert.Contains(eng2, scope);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer staff-jwt-token", handler.LastRequest.Headers.Authorization?.ToString());
        Assert.Contains("/api/engagements?tenantId=tenant-001", handler.LastRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task Staff_WithZeroAssignedEngagements_ReturnsEmptyCollection()
    {
        var (resolver, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
        });

        var staff = CreateUser(nameof(Role.Staff));
        var scope = await resolver.ResolveAllowedEngagementIdsAsync(staff, "tenant-001");

        Assert.NotNull(scope);
        Assert.Empty(scope);
    }

    [Fact]
    public async Task Staff_WorkflowUnreachable_FailsClosedWithDataSourceUnavailable()
    {
        var (resolver, _) = Create(_ => throw new HttpRequestException("Workflow connection refused"));

        var staff = CreateUser(nameof(Role.Staff));
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            resolver.ResolveAllowedEngagementIdsAsync(staff, "tenant-001"));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task Staff_WorkflowReturnsServerError_FailsClosedWithDataSourceUnavailable()
    {
        var (resolver, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var staff = CreateUser(nameof(Role.Staff));
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            resolver.ResolveAllowedEngagementIdsAsync(staff, "tenant-001"));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task Staff_WorkflowReturnsNonArray_FailsClosedWithDataSourceUnavailable()
    {
        var (resolver, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":\"something\"}", System.Text.Encoding.UTF8, "application/json")
        });

        var staff = CreateUser(nameof(Role.Staff));
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            resolver.ResolveAllowedEngagementIdsAsync(staff, "tenant-001"));

        Assert.Equal(ReportErrorKind.DataSourceUnavailable, ex.Kind);
    }

    [Fact]
    public async Task ClientCaller_IsRefusedWithForbidden()
    {
        var (resolver, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var client = CreateUser(nameof(Role.Client));
        var ex = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            resolver.ResolveAllowedEngagementIdsAsync(client, "tenant-001"));

        Assert.Equal(ReportErrorKind.Forbidden, ex.Kind);
    }
}
