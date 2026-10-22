using System.Net;
using System.Text;
using Custodian.Audit.Services.EngagementAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Custodian.Audit.Tests.Unit.EngagementAccess;

/// <summary>
/// WorkflowEngagementAccessClient's access check and engagement list: which Workflow responses mean
/// "allowed", "hidden" or "could not confirm", the path asked, and that the caller's token is forwarded.
/// </summary>
public class WorkflowEngagementAccessCheckTests
{
    private static readonly Guid Tenant = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Engagement = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (WorkflowEngagementAccessClient Client, RecordingHandler Handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond, string? authorization = "Bearer caller-token")
    {
        var handler = new RecordingHandler(respond);
        var http = new DefaultHttpContext();
        if (authorization is not null) http.Request.Headers.Authorization = authorization;
        var client = new WorkflowEngagementAccessClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://workflow") },
            new HttpContextAccessor { HttpContext = http },
            NullLogger<WorkflowEngagementAccessClient>.Instance);
        return (client, handler);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string? json = null) =>
        new(code) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };

    // ---- CanAccessEngagementAsync ----

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task CanAccess_SuccessfulResponse_IsAllowed(HttpStatusCode code)
    {
        var (client, handler) = Create(_ => Status(code));

        Assert.True(await client.CanAccessEngagementAsync(Engagement, Tenant));
        Assert.Equal($"http://workflow/api/Engagements/{Engagement}?tenantId={Tenant}", handler.Request!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal("Bearer caller-token", handler.Request.Headers.Authorization!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task CanAccess_HiddenByWorkflow_IsNotAllowed(HttpStatusCode code)
    {
        var (client, _) = Create(_ => Status(code));

        Assert.False(await client.CanAccessEngagementAsync(Engagement, Tenant));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task CanAccess_AnyOtherResponse_CannotBeConfirmed(HttpStatusCode code)
    {
        var (client, _) = Create(_ => Status(code));

        var error = await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() => client.CanAccessEngagementAsync(Engagement, Tenant));

        Assert.Equal($"Workflow returned {(int)code}.", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoCallerToken_IsNotForwarded(string? authorization)
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.OK), authorization);

        await client.CanAccessEngagementAsync(Engagement, Tenant);

        Assert.False(handler.Request!.Headers.Contains("Authorization"));
    }

    // ---- GetAccessibleEngagementIdsAsync ----

    [Fact]
    public async Task AccessibleIds_AreReadFromTheEngagementList_SkippingRowsWithoutAValidId()
    {
        var other = Guid.NewGuid();
        var json = $"[{{\"engagementId\":\"{Engagement}\"}},{{\"engagementId\":\"not-a-guid\"}},{{\"clientId\":\"x\"}},{{\"engagementId\":\"{other}\"}}]";
        var (client, handler) = Create(_ => Status(HttpStatusCode.OK, json));

        var ids = await client.GetAccessibleEngagementIdsAsync(Tenant);

        Assert.Equal(new HashSet<Guid> { Engagement, other }, ids);
        Assert.Equal($"http://workflow/api/Engagements?tenantId={Tenant}", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer caller-token", handler.Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task AccessibleIds_EmptyList_IsEmpty()
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.OK, "[]"));

        Assert.Empty(await client.GetAccessibleEngagementIdsAsync(Tenant));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AccessibleIds_UnsuccessfulResponse_CannotBeConfirmed(HttpStatusCode code)
    {
        var (client, _) = Create(_ => Status(code));

        var error = await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() => client.GetAccessibleEngagementIdsAsync(Tenant));

        Assert.Equal($"Workflow returned {(int)code}.", error.Message);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"engagementId\":\"x\"}")]
    public async Task AccessibleIds_UnreadableBody_CannotBeConfirmed(string body)
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.OK, body));

        var error = await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() => client.GetAccessibleEngagementIdsAsync(Tenant));

        Assert.Equal("Workflow returned an unreadable engagement list.", error.Message);
        Assert.NotNull(error.InnerException);
    }

    [Fact]
    public async Task WorkflowUnreachable_CannotBeConfirmed()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() => client.GetAccessibleEngagementIdsAsync(Tenant));

        Assert.Equal("Workflow service is unreachable.", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }
}
