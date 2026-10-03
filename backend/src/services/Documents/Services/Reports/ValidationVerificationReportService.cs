using Custodian.Documents.Compliance;
using Custodian.Documents.Data;
using Custodian.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-180: Implementation of the aggregate query engine for document validation and verification metrics.
/// Evaluates live document data within tenant isolation boundaries.
/// </summary>
public class ValidationVerificationReportService : IValidationVerificationReportService
{
    private readonly DocumentDbContext _dbContext;

    public ValidationVerificationReportService(DocumentDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ValidationVerificationData> ComputeAggregateAsync(
        string tenantId,
        IReadOnlyCollection<Guid>? allowedEngagementIds = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        var normalizedTenantId = tenantId.Trim();

        // If a non-null collection of allowed engagement IDs is empty (e.g., staff member with no assigned engagements),
        // return an empty result immediately without querying the database.
        if (allowedEngagementIds != null && allowedEngagementIds.Count == 0)
        {
            return ValidationVerificationData.Empty(normalizedTenantId);
        }

        var query = _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.TenantId == normalizedTenantId && !d.IsDeleted);

        if (allowedEngagementIds != null)
        {
            query = query.Where(d => allowedEngagementIds.Contains(d.EngagementId));
        }

        var records = await query
            .Select(d => new
            {
                d.ComplianceStatus,
                d.VerificationStatus,
                d.RejectionReason,
                d.VerificationReason,
                d.Type
            })
            .ToListAsync(ct);

        var totalUploads = records.Count;

        // Automatic compliance counts (explicit matching ensures unexpected statuses are not misattributed)
        var compliantCount = records.Count(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Compliant, StringComparison.OrdinalIgnoreCase));
        var rejectedComplianceCount = records.Count(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Rejected, StringComparison.OrdinalIgnoreCase));
        var pendingComplianceCount = records.Count(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Pending, StringComparison.OrdinalIgnoreCase));

        var automaticCompliance = new AutomaticComplianceCounts(
            Compliant: compliantCount,
            Rejected: rejectedComplianceCount,
            Pending: pendingComplianceCount);

        // Human verification counts: strictly evaluated ONLY for automatically compliant documents (ComplianceStatus == Compliant)
        var autoCompliantRecords = records
            .Where(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Compliant, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var verifiedCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Verified, StringComparison.OrdinalIgnoreCase));
        var rejectedVerificationCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Rejected, StringComparison.OrdinalIgnoreCase));
        var unverifiedCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Unverified, StringComparison.OrdinalIgnoreCase));
        var pendingVerificationCount = autoCompliantRecords.Count(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Pending, StringComparison.OrdinalIgnoreCase));

        var humanVerification = new HumanVerificationCounts(
            Verified: verifiedCount,
            Rejected: rejectedVerificationCount,
            Unverified: unverifiedCount,
            Pending: pendingVerificationCount);

        // Document-type breakdown: deterministic ordering by count descending, then type ascending
        var byDocumentType = records
            .Where(d => !string.IsNullOrWhiteSpace(d.Type))
            .GroupBy(d => d.Type.Trim())
            .Select(g => new DocumentTypeCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Type, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Automatic compliance rejection reasons: strictly where ComplianceStatus == Rejected and RejectionReason is non-empty
        var automaticRejectionReasons = records
            .Where(d => string.Equals(d.ComplianceStatus, ComplianceStatus.Rejected, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(d.RejectionReason))
            .GroupBy(d => d.RejectionReason!.Trim())
            .Select(g => new RejectionReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Human verification rejection reasons: strictly where ComplianceStatus == Compliant, VerificationStatus == Rejected, and VerificationReason is non-empty
        var humanVerificationRejectionReasons = autoCompliantRecords
            .Where(d => string.Equals(d.VerificationStatus, DocumentVerificationStatus.Rejected, StringComparison.OrdinalIgnoreCase)
                     && !string.IsNullOrWhiteSpace(d.VerificationReason))
            .GroupBy(d => d.VerificationReason!.Trim())
            .Select(g => new RejectionReasonCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Reason, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ValidationVerificationData(
            TenantId: normalizedTenantId,
            TotalUploads: totalUploads,
            AutomaticCompliance: automaticCompliance,
            HumanVerification: humanVerification,
            ByDocumentType: byDocumentType,
            AutomaticRejectionReasons: automaticRejectionReasons,
            HumanVerificationRejectionReasons: humanVerificationRejectionReasons);
    }
}
