namespace Custodian.Audit.Services;

/// <summary>Bound from the "AuditIngestion" section. No key configured = HTTP ingestion refused.</summary>
public sealed class AuditIngestionOptions
{
    public const string SectionName = "AuditIngestion";

    public string? ApiKey { get; set; }
}
