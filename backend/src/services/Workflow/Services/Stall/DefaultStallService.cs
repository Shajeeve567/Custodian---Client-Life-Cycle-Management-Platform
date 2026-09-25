namespace Custodian.Workflow.Services.Stall;

public class DefaultStallService : IStallService
{
    public Task<bool> GetStallStatusAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }
}
