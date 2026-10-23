using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public static string OwnerEmail => GetRequiredCredential("E2E_OWNER_EMAIL");
    public static string OwnerPassword => GetRequiredCredential("E2E_OWNER_PASSWORD");
    public static string StaffEmail => GetRequiredCredential("E2E_STAFF_EMAIL");
    public static string StaffPassword => GetRequiredCredential("E2E_STAFF_PASSWORD");
    public static string ClientEmail => GetRequiredCredential("E2E_CLIENT_EMAIL");
    public static string ClientPassword => GetRequiredCredential("E2E_CLIENT_PASSWORD");
    public static string Stage2ClientEmail => GetRequiredCredential("E2E_STAGE2_CLIENT_EMAIL");
    public static string Stage2ClientPassword => GetRequiredCredential("E2E_STAGE2_CLIENT_PASSWORD");

    public static string IdentityApiUrl => Environment.GetEnvironmentVariable("E2E_IDENTITY_URL") ?? "http://localhost:5281";
    public static string WorkflowApiUrl => Environment.GetEnvironmentVariable("E2E_WORKFLOW_URL") ?? "http://localhost:5225";
    public static string DocumentsApiUrl => Environment.GetEnvironmentVariable("E2E_DOCUMENTS_URL") ?? "http://localhost:5171";
    public static string AuditApiUrl => Environment.GetEnvironmentVariable("E2E_AUDIT_URL") ?? "http://localhost:5051";

    /// <summary>
    /// Retrieves a required E2E credential from the environment.
    /// Throws an InvalidOperationException if the credential is empty or missing,
    /// strictly avoiding any exposure of secret values in exception messages.
    /// </summary>
    public static string GetRequiredCredential(string variableName)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Required E2E credential '{variableName}' is not set. " +
                "Please configure it in 'tests/selenium/.env.e2e.local' for local execution, " +
                "or as a GitHub Actions repository secret.");
        }
        return value;
    }

    /// <summary>
    /// Validates that required E2E credentials are present without printing secret values.
    /// </summary>
    public static void ValidateCredentials(bool includeStage2 = true)
    {
        var requiredVars = new List<string>
        {
            "E2E_OWNER_EMAIL",
            "E2E_OWNER_PASSWORD",
            "E2E_STAFF_EMAIL",
            "E2E_STAFF_PASSWORD",
            "E2E_CLIENT_EMAIL",
            "E2E_CLIENT_PASSWORD"
        };

        if (includeStage2)
        {
            requiredVars.Add("E2E_STAGE2_CLIENT_EMAIL");
            requiredVars.Add("E2E_STAGE2_CLIENT_PASSWORD");
        }

        var missing = requiredVars.Where(v => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v))).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Missing required E2E credentials: {string.Join(", ", missing)}. " +
                "Please configure them in 'tests/selenium/.env.e2e.local' or as environment variables.");
        }
    }

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
