namespace Custodian.Shared.Messaging;

/// <summary>
/// Service-to-service authentication for HTTP audit ingestion (POST /api/audit-events).
/// User tokens cannot write audit events; only services holding the shared ingestion key can.
/// The key is configured per environment (never committed) as "AuditIngestion:ApiKey"
/// (env var AuditIngestion__ApiKey) on the Audit service and on each publishing service.
/// </summary>
public static class AuditIngestion
{
    public const string HeaderName = "X-Audit-Ingestion-Key";
    public const string ConfigKey = "AuditIngestion:ApiKey";

    /// <summary>Keys shorter than this are treated as not configured.</summary>
    public const int MinimumKeyLength = 32;

    /// <summary>
    /// True when the key is a real secret: not blank, long enough, and not the .env.example
    /// placeholder (which is public, so accepting it would let anyone write audit events).
    /// Anything else counts as "not configured", and ingestion fails closed.
    /// </summary>
    public static bool IsUsableKey(string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && key.Length >= MinimumKeyLength
        && !key.StartsWith('<')
        && !key.Contains("FILL_IN_HERE", StringComparison.OrdinalIgnoreCase);
}
