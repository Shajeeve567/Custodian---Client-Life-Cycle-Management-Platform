using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Custodian.Shared.Http;

public static class CorsExtensions
{
    private static readonly string[] ExposedHeaders = ["Content-Disposition", "X-Tenant-ID", "X-Total-Count"];

    /// <summary>
    /// Configures the standard CORS policy for Custodian microservices.
    /// - Origins listed in 'Cors:AllowedOrigins' (env Cors__AllowedOrigins__0, ...) or 'CORS_ALLOWED_ORIGINS'
    ///   (comma/semicolon separated) are allowed in every environment.
    /// - With none configured, Development and Testing allow any origin (local frontends on any port).
    /// - With none configured anywhere else, no cross-origin caller is allowed: the frontend's origin must
    ///   be configured explicitly (previously every origin was allowed in production too).
    /// </summary>
    public static IServiceCollection AddCustodianCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredOrigins = ConfiguredOrigins(configuration);

        services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                if (configuredOrigins.Length > 0)
                {
                    policy.WithOrigins(configuredOrigins);
                }
                else if (IsDevelopmentOrTesting(environment))
                {
                    policy.AllowAnyOrigin();
                }
                else
                {
                    policy.SetIsOriginAllowed(_ => false);
                }

                policy.AllowAnyHeader()
                      .AllowAnyMethod()
                      .WithExposedHeaders(ExposedHeaders)
                      .SetPreflightMaxAge(TimeSpan.FromHours(24));
            });
        });

        return services;
    }

    /// <summary>Logs which origins are allowed; warns when a deployed service allows none.</summary>
    public static void LogCustodianCors(this WebApplication app)
    {
        var origins = ConfiguredOrigins(app.Configuration);
        if (origins.Length > 0)
        {
            app.Logger.LogInformation("CORS allows origins: {Origins}", string.Join(", ", origins));
        }
        else if (IsDevelopmentOrTesting(app.Environment))
        {
            app.Logger.LogInformation("CORS allows any origin ({Environment}, no Cors:AllowedOrigins configured).", app.Environment.EnvironmentName);
        }
        else
        {
            app.Logger.LogWarning(
                "CORS allows NO cross-origin callers: set Cors__AllowedOrigins__0 (e.g. the frontend's https URL) for the {Environment} environment.",
                app.Environment.EnvironmentName);
        }
    }

    private static string[] ConfiguredOrigins(IConfiguration configuration) =>
        (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
         ?? configuration["CORS_ALLOWED_ORIGINS"]?.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         ?? [])
        .Where(o => !string.IsNullOrWhiteSpace(o))
        .Select(o => o.Trim().TrimEnd('/'))
        .ToArray();

    private static bool IsDevelopmentOrTesting(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");
}
