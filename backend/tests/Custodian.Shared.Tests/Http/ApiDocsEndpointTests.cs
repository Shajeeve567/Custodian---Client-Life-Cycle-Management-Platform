using System.Net;
using System.Text.Json;
using Custodian.Shared.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Custodian.Shared.Tests.Http;

/// <summary>API docs are always on in Development, opt-in elsewhere, and describe the JWT Bearer scheme.</summary>
public class ApiDocsEndpointTests
{
    private const string SwaggerUiIndex = ApiDocsExtensions.SwaggerUiPath + "/index.html";

    private static async Task<HttpClient> StartAsync(string environment, string? enabled = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        if (enabled != null)
        {
            builder.Configuration[ApiDocsExtensions.EnabledConfig] = enabled;
        }

        builder.Services.AddCustodianApiDocs();
        var app = builder.Build();
        app.UseCustodianApiDocs();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync();
        return app.GetTestClient();
    }

    public static TheoryData<string> DocsPaths => new()
    {
        ApiDocsExtensions.OpenApiPath,
        SwaggerUiIndex,
        ApiDocsExtensions.ScalarPath
    };

    [Theory]
    [MemberData(nameof(DocsPaths))]
    public async Task Development_ServesDocsWithoutFlag(string path)
    {
        var client = await StartAsync("Development");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("Production", null)]
    [InlineData("Production", "false")]
    [InlineData("Staging", null)]
    public async Task Deployed_WithoutFlag_HasNoDocs(string environment, string? enabled)
    {
        var client = await StartAsync(environment, enabled);

        foreach (var path in DocsPaths)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ping")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(DocsPaths))]
    public async Task Deployed_WithFlag_ServesDocs(string path)
    {
        var client = await StartAsync("Production", "true");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Document_DeclaresBearerSchemeAndNoServers()
    {
        var client = await StartAsync("Production", "true");

        using var document = JsonDocument.Parse(await client.GetStringAsync(ApiDocsExtensions.OpenApiPath));
        var root = document.RootElement;

        var scheme = root.GetProperty("components").GetProperty("securitySchemes").GetProperty(ApiDocsExtensions.BearerScheme);
        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
        Assert.True(root.GetProperty("security")[0].TryGetProperty(ApiDocsExtensions.BearerScheme, out _));
        Assert.False(root.TryGetProperty("servers", out var servers) && servers.GetArrayLength() > 0);
    }

    [Fact]
    public async Task SwaggerUi_PointsAtOpenApiDocument()
    {
        var client = await StartAsync("Production", "true");

        var config = await client.GetStringAsync(ApiDocsExtensions.SwaggerUiPath + "/index.js");

        Assert.Contains(ApiDocsExtensions.OpenApiPath, config);
    }
}
