using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services;

public interface IClientPortalService
{
    /// <summary>
    /// Aggregates live workflow data into a client-safe dashboard DTO for a specific engagement.
    /// If clientId is provided, validates that the engagement belongs to that client.
    /// </summary>
    Task<ClientPortalDashboardDto?> GetDashboardForEngagementAsync(Guid engagementId, string tenantId, string? clientId = null);

    /// <summary>
    /// Automatically resolves the active engagement for the given client and aggregates the dashboard data.
    /// </summary>
    Task<ClientPortalDashboardDto?> GetActiveDashboardForClientAsync(string tenantId, string clientId);
}
