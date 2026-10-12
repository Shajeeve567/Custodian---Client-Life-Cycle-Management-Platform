using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IReadinessService
{
    /// <summary>
    /// Evaluates whether an engagement is ready for handoff or closure. Read-only;
    /// reuses the existing gate evaluator and next-action engine so the answer is
    /// always consistent with what those services report.
    /// </summary>
    Task<ReadinessResponse> EvaluateAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}