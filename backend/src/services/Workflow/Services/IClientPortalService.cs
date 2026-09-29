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
    /// <param name="staffId">When set (a Staff caller previewing), only engagements that staff member is responsible for.</param>
    Task<ClientPortalDashboardDto?> GetActiveDashboardForClientAsync(string tenantId, string clientId, string? staffId = null);

    /// <summary>
    /// Resolves the latest active engagement in the tenant (used for staff previewing client portals).
    /// </summary>
    /// <param name="staffId">When set (a Staff caller), only engagements that staff member is responsible for.</param>
    Task<ClientPortalDashboardDto?> GetActiveDashboardForTenantAsync(string tenantId, string? staffId = null);
}

