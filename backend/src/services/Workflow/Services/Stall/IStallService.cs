namespace Custodian.Workflow.Services.Stall;

public interface IStallService
{
    Task<bool> GetStallStatusAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}
