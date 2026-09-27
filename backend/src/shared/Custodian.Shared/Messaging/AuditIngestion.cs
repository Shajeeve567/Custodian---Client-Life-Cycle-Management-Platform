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
}
