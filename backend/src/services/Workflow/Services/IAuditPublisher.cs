using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

public interface IAuditPublisher
{
    Task PublishEventAsync(Guid engagementId, string tenantId, string actor, string type, object payload);
    Task PublishGenesisEventAsync(Engagement engagement, string tenantId);
}
