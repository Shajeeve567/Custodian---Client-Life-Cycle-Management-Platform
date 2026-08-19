using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Custodian.Shared.Auth;

public sealed class DevelopmentAuthBypassOptions
{
    public const string Section = "Auth";
    public const string EnabledKey = "Auth:Bypass";

    public bool Enabled { get; init; }
}

public static class DevelopmentAuthBypass
{
    public static IServiceCollection AddDevelopmentAuthBypass(this IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton(new DevelopmentAuthBypassOptions
        {
            Enabled = configuration.GetValue<bool>(DevelopmentAuthBypassOptions.EnabledKey)
        });

    public static IApplicationBuilder UseDevelopmentAuthBypass(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var options = context.RequestServices.GetRequiredService<DevelopmentAuthBypassOptions>();
            if (options.Enabled && context.User.Identity?.IsAuthenticated != true)
            {
                var tenantId = context.Request.Headers["X-Tenant-ID"].FirstOrDefault() ?? "dev-tenant";
                var role = context.Request.Headers["X-Dev-Role"].FirstOrDefault() ?? "staff";
                var name = context.Request.Headers["X-Dev-User"].FirstOrDefault() ?? "dev-user";

                var claims = new[]
                {
                    new Claim("sub", name),
                    new Claim(ClaimTypes.Name, name),
                    new Claim("tenant_id", tenantId),
                    new Claim(ClaimTypes.Role, role)
                };

                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "DevelopmentBypass"));
            }

            await next();
        });
}