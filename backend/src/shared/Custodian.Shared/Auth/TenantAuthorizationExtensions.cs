using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Custodian.Shared.Auth;

/// <summary>
/// Tenant-scoped authorization for the Workflow, Documents and Audit APIs.
///
/// Identity issues two kinds of token with the same issuer, audience and key: a global login token
/// (sub + email only, used to list and select a workspace) and a workspace token (adds tenant_id and
/// role). Without this policy the global token was accepted by tenant APIs, and because every
/// controller falls back to the X-Tenant-ID header when the JWT has no tenant claim, any registered
/// user could act inside any tenant.
///
/// Two layers enforce a non-empty tenant_id claim:
///  - the default policy (used by a plain [Authorize]);
///  - the named policy <see cref="PolicyName"/>, applied to every controller endpoint with
///    <see cref="RequireTenantMembership"/>. This matters because [Authorize(Roles = ...)] does not
///    use the default policy; endpoint-level policies are combined with it instead. [AllowAnonymous]
///    endpoints (e.g. service-key audit ingestion) still bypass both.
/// Identity must NOT use this: its workspace-selection endpoints accept the login token.
/// </summary>
public static class TenantAuthorizationExtensions
{
    public const string TenantClaimType = "tenant_id";
    public const string PolicyName = "TenantMember";

    public static IServiceCollection AddTenantScopedAuthorization(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            var tenantPolicy = BuildTenantPolicy();
            options.DefaultPolicy = tenantPolicy;
            options.AddPolicy(PolicyName, tenantPolicy);
        });

        return services;
    }

    /// <summary>Applies the tenant policy to all controller endpoints (combined with their own [Authorize] data).</summary>
    public static ControllerActionEndpointConventionBuilder RequireTenantMembership(this ControllerActionEndpointConventionBuilder builder) =>
        builder.RequireAuthorization(PolicyName);

    public static AuthorizationPolicy BuildTenantPolicy() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
                !string.IsNullOrWhiteSpace(context.User.FindFirst(TenantClaimType)?.Value))
            .Build();
}
