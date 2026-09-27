using System.Security.Claims;
using Custodian.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Custodian.Shared.Tests.Auth;

/// <summary>
/// C1: tenant APIs must reject Identity's global login token (sub + email, no tenant_id). Otherwise a
/// signed-in user with no workspace could pick any tenant through the X-Tenant-ID header.
/// </summary>
public class TenantAuthorizationPolicyTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTenantScopedAuthorization();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal User(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth"));

    private static async Task<bool> AuthorizeAsync(ServiceProvider provider, ClaimsPrincipal user, params IAuthorizeData[] authorizeData)
    {
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorizeData);
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy!);
        return result.Succeeded;
    }

    [Fact]
    public async Task GlobalLoginToken_WithoutTenantClaim_IsRejectedByPlainAuthorize()
    {
        using var provider = BuildProvider();
        var loginToken = User(new Claim("sub", "user-1"), new Claim("email", "a@b.c"));

        Assert.False(await AuthorizeAsync(provider, loginToken, new AuthorizeAttribute()));
    }

    // What a controller endpoint carries in production: its own [Authorize(Roles=...)] plus the tenant
    // policy added to every controller by MapControllers().RequireTenantMembership().
    private static IAuthorizeData[] RoleEndpoint(string roles) =>
        new IAuthorizeData[] { new AuthorizeAttribute(TenantAuthorizationExtensions.PolicyName), new AuthorizeAttribute { Roles = roles } };

    [Fact]
    public async Task RoleOnlyAuthorize_DoesNotUseDefaultPolicy_WhichIsWhyTheEndpointConventionIsNeeded()
    {
        using var provider = BuildProvider();
        var noTenantButStaff = User(new Claim("sub", "user-1"), new Claim(ClaimTypes.Role, "Staff"));

        // ASP.NET Core skips the default policy once Roles are given...
        Assert.True(await AuthorizeAsync(provider, noTenantButStaff, new AuthorizeAttribute { Roles = "Owner,Staff" }));
        // ...so the tenant policy is applied to every endpoint explicitly, and then the token is rejected.
        Assert.False(await AuthorizeAsync(provider, noTenantButStaff, RoleEndpoint("Owner,Staff")));
    }

    [Fact]
    public async Task RoleEndpoint_RequiresBothTenantAndRole()
    {
        using var provider = BuildProvider();
        var tenant = Guid.NewGuid().ToString();

        Assert.True(await AuthorizeAsync(provider, User(new Claim("tenant_id", tenant), new Claim(ClaimTypes.Role, "Staff")), RoleEndpoint("Owner,Staff")));
        Assert.False(await AuthorizeAsync(provider, User(new Claim("tenant_id", tenant), new Claim(ClaimTypes.Role, "Client")), RoleEndpoint("Owner,Staff")));
    }

    [Fact]
    public async Task WorkspaceToken_WithTenantClaim_IsAccepted()
    {
        using var provider = BuildProvider();
        var workspaceToken = User(new Claim("sub", "user-1"), new Claim("tenant_id", Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, "Staff"));

        Assert.True(await AuthorizeAsync(provider, workspaceToken, new AuthorizeAttribute()));
        Assert.True(await AuthorizeAsync(provider, workspaceToken, new AuthorizeAttribute { Roles = "Owner,Staff" }));
    }

    [Fact]
    public async Task EmptyTenantClaim_IsRejected()
    {
        using var provider = BuildProvider();

        Assert.False(await AuthorizeAsync(provider, User(new Claim("sub", "u"), new Claim("tenant_id", "  ")), new AuthorizeAttribute()));
    }

    [Fact]
    public async Task AnonymousCaller_IsRejected()
    {
        using var provider = BuildProvider();

        Assert.False(await AuthorizeAsync(provider, new ClaimsPrincipal(new ClaimsIdentity()), new AuthorizeAttribute()));
    }
}
