using System.Security.Claims;

namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-182: Resolves engagement visibility scope for report generation.
/// Owner callers aggregate active documents across the authenticated tenant (null allowedEngagementIds).
/// Staff callers aggregate only engagements assigned to them in Workflow (non-null allowedEngagementIds).
/// </summary>
public interface IReportEngagementScopeResolver
{
    /// <summary>
    /// Resolves allowed engagement IDs for the caller:
    /// - Owner: returns null (unrestricted tenant scope).
    /// - Staff: queries Workflow GET /api/engagements using the caller's JWT, returning assigned engagement IDs.
    /// - Fails closed: throws ReportGenerationException.DataSourceUnavailable if Workflow is unreachable or fails.
    /// - Throws ReportGenerationException.Forbidden if caller is not Owner or Staff.
    /// </summary>
    Task<IReadOnlyCollection<Guid>?> ResolveAllowedEngagementIdsAsync(ClaimsPrincipal user, string tenantId, CancellationToken ct = default);
}
