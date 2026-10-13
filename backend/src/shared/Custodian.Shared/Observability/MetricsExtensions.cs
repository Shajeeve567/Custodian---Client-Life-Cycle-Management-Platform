using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Prometheus;

namespace Custodian.Shared.Observability;

public static class MetricsExtensions
{
    public const string Path = "/metrics";
    public const string ScrapeKeyConfig = "Metrics:ScrapeKey";
    public const string ScrapeUser = "prometheus";

    /// <summary>
    /// Records HTTP request metrics (rate, duration, status, controller/action) and exposes them, with the
    /// .NET runtime and process metrics, in Prometheus format at GET /metrics.
    /// - 'Metrics:ScrapeKey' set (env Metrics__ScrapeKey): /metrics requires HTTP Basic auth with user
    ///   'prometheus' and that key as the password. Basic, not Bearer, so the JWT handler never sees it.
    /// - not set: /metrics is open in Development/Testing and returns 404 everywhere else, so a deployed
    ///   service never publishes its metrics unauthenticated.
    /// Call it right after Build(), before the other middleware.
    /// </summary>
    public static WebApplication UseCustodianMetrics(this WebApplication app)
    {
        var scrapeKey = app.Configuration[ScrapeKeyConfig];
        var enabled = !string.IsNullOrWhiteSpace(scrapeKey) ||
                      app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing");

        app.UseHttpMetrics();
        if (!enabled)
        {
            return app;
        }

        if (!string.IsNullOrWhiteSpace(scrapeKey))
        {
            app.UseWhen(context => context.Request.Path.StartsWithSegments(Path), branch => branch.Use(async (context, next) =>
            {
                if (!HasScrapeCredentials(context.Request, scrapeKey))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Basic realm=\"metrics\"";
                    return;
                }

                await next();
            }));
        }

        app.MapMetrics(Path);
        return app;
    }

    private static bool HasScrapeCredentials(HttpRequest request, string scrapeKey)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header) ||
            !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return false;
        }

        string credentials;
        try
        {
            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes($"{ScrapeUser}:{scrapeKey}");
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(credentials), expected);
    }
}
