namespace Custodian.Workflow.Services;

public interface IAuditPublisher
{
    Task PublishEventAsync(Guid engagementId, string tenantId, string actor, string type, object payload);
}
