namespace Custodian.Audit.Services.Reports;

using Custodian.Shared.Reporting.Models;

/// <summary>
/// CSTD-248: Proof-of-Delivery report model containing report metadata, visual report sections (PDF),
/// and the full-fidelity detail table (CSV export) with complete 64-character SHA-256 hashes.
/// </summary>
public sealed class ProofOfDeliveryReport : ReportModel
{
    public ProofOfDeliveryReport(
        ReportMetadata metadata,
        IEnumerable<ReportSection> sections,
        TableSection detail)
        : base(metadata, sections)
    {
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
        Orientation = ReportPageOrientation.Landscape;
    }

    /// <summary>
    /// One row per audit event in the timeline, preserving full 64-character SHA-256 hashes for CSV export.
    /// </summary>
    public TableSection Detail { get; }
}
