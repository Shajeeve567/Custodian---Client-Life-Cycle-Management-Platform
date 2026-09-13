using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Custodian.Shared.Tenancy;

public sealed class TenantContext
{
    public const string ClaimName = "tenant_id";
    public const string HeaderName = "X-Tenant-ID";

    public string? TenantId { get; set; }

    public string RequireTenantId() =>
        TenantId ?? throw new InvalidOperationException("Tenant context is not available on this request.");
}

public static class TenantContextExtensions
{
    public static IServiceCollection AddTenantContext(this IServiceCollection services) =>
        services.AddScoped<TenantContext>();

    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var tenantContext = context.RequestServices.GetRequiredService<TenantContext>();
            var claimTenant = context.User.FindFirst(TenantContext.ClaimName)?.Value
                           ?? context.User.FindFirst("tenantId")?.Value;
            var headerTenant = context.Request.Headers[TenantContext.HeaderName].FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(claimTenant))
            {
                var cleanClaimTenant = claimTenant.Trim();
                if (!string.IsNullOrWhiteSpace(headerTenant) &&
                    !string.Equals(headerTenant.Trim(), cleanClaimTenant, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"message\":\"Cross-tenant access forbidden. Caller does not belong to the requested tenant.\"}");
                    return;
                }

                tenantContext.TenantId = cleanClaimTenant;
            }
            else
            {
                tenantContext.TenantId = headerTenant?.Trim();
            }

            await next();
        });
}