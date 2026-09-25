using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services.NextAction;

public interface INextActionService
{
    Task<NextActionResult?> GetNextActionAsync(
        Guid engagementId,
        string tenantId,
        NextActionView view,
        CancellationToken ct = default);
}
