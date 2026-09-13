namespace Custodian.Workflow.DTOs;

/// <summary>
/// Workflow's own copy of the subset of a document's shape needed for gate evaluation.
/// Deserialized from the Documents service's GET /api/engagements/{id}/documents response.
/// Intentionally local rather than a shared-project reference — services communicate over
/// HTTP, not via shared C# types, for their own domain DTOs (KafkaEnvelope-style shared
/// contracts are reserved for genuinely cross-cutting infrastructure).
/// </summary>
public sealed class DocumentSummaryDto
{
    public Guid DocumentId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string ComplianceStatus { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}
