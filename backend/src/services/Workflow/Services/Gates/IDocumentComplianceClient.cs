using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services.Gates;

public interface IDocumentComplianceClient
{
    /// <summary>
    /// Fetches the documents on record for an engagement from the Documents service.
    /// Throws DocumentComplianceUnavailableException if the Documents service cannot be
    /// reached or returns an error — callers should fail closed on this, not treat it as
    /// "no documents".
    /// </summary>
    Task<IReadOnlyList<DocumentSummaryDto>> GetDocumentsAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}

/// <summary>
/// Thrown when the Documents service cannot be reached or errors, so gate evaluation can
/// fail closed (block the transition) rather than silently treating it as "no documents".
/// </summary>
public sealed class DocumentComplianceUnavailableException : Exception
{
    public DocumentComplianceUnavailableException(string message) : base(message) { }
    public DocumentComplianceUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}
