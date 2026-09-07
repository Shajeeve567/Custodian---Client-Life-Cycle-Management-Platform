using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Custodian.Shared.Http;

public static class CorsExtensions
{
    /// <summary>
    /// Configures standardized CORS policy for Custodian microservices.
    /// In production, respects 'Cors:AllowedOrigins' or 'CORS_ALLOWED_ORIGINS' environment variable.
    /// In local development (no origins specified), permits all origins with full headers and methods.
    /// </summary>
    public static IServiceCollection AddCustodianCors(this IServiceCollection services, IConfiguration configuration)
    {
        var configuredOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? configuration["CORS_ALLOWED_ORIGINS"]?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (configuredOrigins != null && configuredOrigins.Length > 0)
                {
                    policy.WithOrigins(configuredOrigins)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .WithExposedHeaders("Content-Disposition", "X-Tenant-ID")
                          .SetPreflightMaxAge(TimeSpan.FromHours(24));
                }
                else
                {
                    policy.AllowAnyOrigin()
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .WithExposedHeaders("Content-Disposition", "X-Tenant-ID")
                          .SetPreflightMaxAge(TimeSpan.FromHours(24));
                }
            });
        });

        return services;
    }
}
