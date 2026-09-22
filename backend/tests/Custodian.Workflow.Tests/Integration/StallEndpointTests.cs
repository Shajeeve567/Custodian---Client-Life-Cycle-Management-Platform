using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// Black-box test of the stall endpoint through the full middleware pipeline:
/// routing, authentication, tenant resolution, service, response shape.
/// Uses a real Workflow service host. Run with the local MySQL stack up.
/// </summary>
public class StallEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StallEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetStall_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/stall");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetStall_NonexistentEngagement_Returns404()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(Guid.NewGuid()));

        var response = await client.GetAsync($"/api/engagements/{Guid.NewGuid()}/stall");

        // 404 because the engagement doesn't exist in this tenant.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}