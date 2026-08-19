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
            tenantContext.TenantId =
                context.User.FindFirstValue(TenantContext.ClaimName)
                ?? context.Request.Headers[TenantContext.HeaderName].FirstOrDefault();

            await next();
        });
}