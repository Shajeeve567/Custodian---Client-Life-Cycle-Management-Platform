using System.Security.Claims;
using Custodian.Shared.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Custodian.Shared.Tests.Tenancy;

public class TenantContextMiddlewareTests
{
    private static (DefaultHttpContext context, TenantContext tenantContext) CreateContextWithServices(
        ClaimsPrincipal? user = null, 
        string? headerTenantId = null)
    {
        var services = new ServiceCollection();
        services.AddTenantContext();
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
            User = user ?? new ClaimsPrincipal(new ClaimsIdentity())
        };

        if (!string.IsNullOrWhiteSpace(headerTenantId))
        {
            httpContext.Request.Headers[TenantContext.HeaderName] = headerTenantId;
        }

        var tenantContext = serviceProvider.GetRequiredService<TenantContext>();
        return (httpContext, tenantContext);
    }

    [Fact]
    public async Task UseTenantContext_MismatchedHeaderAndClaim_Returns403Forbidden()
    {
        // Arrange: User authenticated with tenant-A, but header says tenant-B
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-A")
        }, "TestAuth"));

        var (context, tenantContext) = CreateContextWithServices(user, headerTenantId: "tenant-B");

        var appBuilder = new ApplicationBuilder(context.RequestServices);
        appBuilder.UseTenantContext();
        var pipeline = appBuilder.Build();

        // Act
        await pipeline(context);

        // Assert
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task UseTenantContext_MatchingClaimAndHeader_AllowsAndSetsTenantId()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-A")
        }, "TestAuth"));

        var (context, tenantContext) = CreateContextWithServices(user, headerTenantId: "tenant-A");

        var appBuilder = new ApplicationBuilder(context.RequestServices);
        appBuilder.UseTenantContext();
        bool nextCalled = false;
        appBuilder.Run(_ => { nextCalled = true; return Task.CompletedTask; });
        var pipeline = appBuilder.Build();

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal("tenant-A", tenantContext.TenantId);
    }

    [Fact]
    public async Task UseTenantContext_ClaimOnly_SetsTenantId()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-A")
        }, "TestAuth"));

        var (context, tenantContext) = CreateContextWithServices(user);

        var appBuilder = new ApplicationBuilder(context.RequestServices);
        appBuilder.UseTenantContext();
        bool nextCalled = false;
        appBuilder.Run(_ => { nextCalled = true; return Task.CompletedTask; });
        var pipeline = appBuilder.Build();

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal("tenant-A", tenantContext.TenantId);
    }

    [Fact]
    public async Task UseTenantContext_HeaderOnlyUnauthenticated_SetsTenantId()
    {
        // Arrange
        var (context, tenantContext) = CreateContextWithServices(headerTenantId: "tenant-A");

        var appBuilder = new ApplicationBuilder(context.RequestServices);
        appBuilder.UseTenantContext();
        bool nextCalled = false;
        appBuilder.Run(_ => { nextCalled = true; return Task.CompletedTask; });
        var pipeline = appBuilder.Build();

        // Act
        await pipeline(context);

        // Assert
        Assert.True(nextCalled);
        Assert.Equal("tenant-A", tenantContext.TenantId);
    }
}
