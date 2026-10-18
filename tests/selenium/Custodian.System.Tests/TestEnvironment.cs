using System;
using System.IO;
using System.Threading;

namespace Custodian.System.Tests;

/// <summary>
/// Manages E2E test configuration and automatically loads credentials from .env.e2e.local.
/// Enables seamless environment switching between local (localhost:3000) and deployed staging,
/// as well as interactive demonstration mode for live review.
/// </summary>
public static class TestEnvironment
{
    private static bool _initialized;
    private static readonly object _lock = new();

    public static string BaseUrl => Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:3000";
    public static string TenantName => Environment.GetEnvironmentVariable("E2E_TENANT_NAME") ?? "SE3112 E2E Testing Agency";
    public static string OwnerEmail => Environment.GetEnvironmentVariable("E2E_OWNER_EMAIL") ?? "";
    public static string OwnerPassword => Environment.GetEnvironmentVariable("E2E_OWNER_PASSWORD") ?? "";
    public static string StaffEmail => Environment.GetEnvironmentVariable("E2E_STAFF_EMAIL") ?? "";
    public static string StaffPassword => Environment.GetEnvironmentVariable("E2E_STAFF_PASSWORD") ?? "";
    public static string ClientEmail => Environment.GetEnvironmentVariable("E2E_CLIENT_EMAIL") ?? "";
    public static string ClientPassword => Environment.GetEnvironmentVariable("E2E_CLIENT_PASSWORD") ?? "";

    /// <summary>
    /// When true, Chrome runs visibly and pauses briefly during key UI actions
    /// so the evaluator can observe live execution during the SE3112 demonstration.
    /// </summary>
    public static bool IsDemoMode => string.Equals(Environment.GetEnvironmentVariable("SELENIUM_DEMO_MODE"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Duration in milliseconds for demonstration pauses (default 1500ms).
    /// </summary>
    public static int DemoDelayMs => int.TryParse(Environment.GetEnvironmentVariable("SELENIUM_DEMO_DELAY_MS"), out var d) ? d : 1500;

    /// <summary>
    /// Pauses execution only when SELENIUM_DEMO_MODE=true. Never affects standard or CI runs.
    /// </summary>
    public static void DemoPause(int? customDelayMs = null)
    {
        if (IsDemoMode)
        {
            Thread.Sleep(customDelayMs ?? DemoDelayMs);
        }
    }

    public static void Initialize()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            LoadDotEnv();
            _initialized = true;
        }
    }

    private static void LoadDotEnv()
    {
        var currentDir = new DirectoryInfo(AppContext.BaseDirectory);
        string? envPath = null;

        while (currentDir != null)
        {
            var candidate = Path.Combine(currentDir.FullName, ".env.e2e.local");
            if (File.Exists(candidate))
            {
                envPath = candidate;
                break;
            }
            var candidateInSelenium = Path.Combine(currentDir.FullName, "tests", "selenium", ".env.e2e.local");
            if (File.Exists(candidateInSelenium))
            {
                envPath = candidateInSelenium;
                break;
            }
            currentDir = currentDir.Parent;
        }

        if (envPath != null && File.Exists(envPath))
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex > 0)
                {
                    var key = trimmed.Substring(0, separatorIndex).Trim();
                    var value = trimmed.Substring(separatorIndex + 1).Trim();
                    if ((value.StartsWith('"') && value.EndsWith('"')) ||
                        (value.StartsWith('\'') && value.EndsWith('\'')))
                    {
                        value = value.Substring(1, value.Length - 2);
                    }
                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }
            }
        }
    }
}
