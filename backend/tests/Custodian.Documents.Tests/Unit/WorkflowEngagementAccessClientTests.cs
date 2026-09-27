using System.Net;
using Custodian.Documents.Services.EngagementAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

/// <summary>C7: Documents asks Workflow (as the caller) whether the caller may access an engagement.</summary>
public class WorkflowEngagementAccessClientTests
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

    private static (WorkflowEngagementAccessClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer client-token";
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var client = new WorkflowEngagementAccessClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://workflow.test") },
            accessor,
            NullLogger<WorkflowEngagementAccessClient>.Instance);
        return (client, handler);
    }

    [Fact]
    public async Task Ok_Allows_AndForwardsCallersToken()
    {
        var engagementId = Guid.NewGuid();
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.True(await client.CanAccessEngagementAsync(engagementId, "tenant-001"));
        Assert.Equal("Bearer client-token", handler.LastRequest!.Headers.Authorization!.ToString());
        Assert.Contains($"/api/engagements/{engagementId}", handler.LastRequest.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Denials_ReturnFalse(HttpStatusCode status)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status));

        Assert.False(await client.CanAccessEngagementAsync(Guid.NewGuid(), "tenant-001"));
    }

    [Fact]
    public async Task ServerError_Throws_SoCallersFailClosed()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() => client.CanAccessEngagementAsync(Guid.NewGuid(), "tenant-001"));
    }

    [Fact]
    public async Task Unreachable_Throws_SoCallersFailClosed()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() => client.CanAccessEngagementAsync(Guid.NewGuid(), "tenant-001"));
    }

    [Fact]
    public async Task GetEngagementClientId_ReadsClientIdFromWorkflow()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"engagementId\":\"x\",\"clientId\":\"client-42\"}", System.Text.Encoding.UTF8, "application/json")
        });

        Assert.Equal("client-42", await client.GetEngagementClientIdAsync(Guid.NewGuid(), "tenant-001"));
        Assert.Equal("Bearer client-token", handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetEngagementClientId_ReturnsNullInsteadOfThrowing(HttpStatusCode status)
    {
        var (client, _) = Create(_ => new HttpResponseMessage(status));

        Assert.Null(await client.GetEngagementClientIdAsync(Guid.NewGuid(), "tenant-001"));
    }

    [Fact]
    public async Task GetEngagementClientId_WorkflowUnreachable_ReturnsNull()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        Assert.Null(await client.GetEngagementClientIdAsync(Guid.NewGuid(), "tenant-001"));
    }
}
