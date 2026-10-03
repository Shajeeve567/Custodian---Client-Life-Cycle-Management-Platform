using System.Globalization;
using Custodian.Shared.Reporting.Models;

namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-182: Validation and verification report model containing metadata, PDF visual sections, and the CSV detail table.
/// </summary>
public sealed class ValidationVerificationReport : ReportModel
{
    public ValidationVerificationReport(
        ReportMetadata metadata,
        IEnumerable<ReportSection> sections,
        TableSection detail)
        : base(metadata, sections)
    {
        Detail = detail;
    }

    /// <summary>One row per aggregate metric in the population (for CSV export).</summary>
    public TableSection Detail { get; }
}

/// <summary>
/// CSTD-182: Builds deterministic report sections and the CSV detail table from <see cref="ValidationVerificationData"/>.
/// Automatic compliance and human verification are kept strictly separate.
/// </summary>
public static class ValidationVerificationReportBuilder
{
    public const string ReportCode = "VALIDATION_VERIFICATION";
    public const string Title = "Validation & Verification";
    public const string DataSource = "Documents service live database";

    public static ValidationVerificationReport Build(
        ValidationVerificationData data,
        string tenantId,
        string generatedBy,
        ValidationVerificationFilter? filter,
        DateTimeOffset generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedBy);

        var metadata = new ReportMetadata(
            ReportCode,
            Title,
            tenantId,
            generatedAtUtc,
            generatedBy,
            AppliedFilters(filter),
            DataSource);

        var sections = new List<ReportSection>
        {
            Summary(data),
            AutomaticCompliance(data),
            HumanVerification(data),
            DocumentTypeBreakdown(data),
            AutomaticRejectionReasons(data),
            HumanVerificationRejectionReasons(data)
        };

        return new ValidationVerificationReport(metadata, sections, Detail(data));
    }

    public static string EngagementCode(Guid engagementId) =>
        $"ENG-{engagementId.ToString("N")[..6].ToUpperInvariant()}";

    private static IEnumerable<KeyValuePair<string, string>> AppliedFilters(ValidationVerificationFilter? filter) =>
    [
        new("From", filter?.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "All"),
        new("To", filter?.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "All"),
        new("Engagement", filter?.EngagementId is { } id ? EngagementCode(id) : "All")
    ];

    private static ReportSection Summary(ValidationVerificationData data) => new KeyValueSection("Summary",
    [
        new("Total uploads", data.TotalUploads)
    ]);

    private static ReportSection AutomaticCompliance(ValidationVerificationData data) => new KeyValueSection("Automatic compliance",
    [
        new("Compliant", data.AutomaticCompliance.Compliant),
        new("Rejected", data.AutomaticCompliance.Rejected),
        new("Pending", data.AutomaticCompliance.Pending)
    ]);

    private static ReportSection HumanVerification(ValidationVerificationData data) => new KeyValueSection("Human verification",
    [
        new("Verified", data.HumanVerification.Verified),
        new("Rejected", data.HumanVerification.Rejected),
        new("Unverified", data.HumanVerification.Unverified),
        new("Pending", data.HumanVerification.Pending)
    ]);

    private static ReportSection DocumentTypeBreakdown(ValidationVerificationData data)
    {
        const string title = "Document type breakdown";
        if (data.ByDocumentType.Count == 0)
        {
            return new EmptySection(title, "No document types recorded.");
        }

        return new TableSection(
            title,
            [new ReportColumn("Document type", Width: 2), new ReportColumn("Count", ReportColumnAlignment.Right)],
            data.ByDocumentType.Select(d => new object?[] { d.Type, d.Count }));
    }

    private static ReportSection AutomaticRejectionReasons(ValidationVerificationData data)
    {
        const string title = "Automatic rejection reasons";
        if (data.AutomaticRejectionReasons.Count == 0)
        {
            return new EmptySection(title, "No automatic rejections recorded.");
        }

        return new TableSection(
            title,
            [new ReportColumn("Reason", Width: 2), new ReportColumn("Count", ReportColumnAlignment.Right)],
            data.AutomaticRejectionReasons.Select(r => new object?[] { r.Reason, r.Count }));
    }

    private static ReportSection HumanVerificationRejectionReasons(ValidationVerificationData data)
    {
        const string title = "Human verification rejection reasons";
        if (data.HumanVerificationRejectionReasons.Count == 0)
        {
            return new EmptySection(title, "No human verification rejections recorded.");
        }

        return new TableSection(
            title,
            [new ReportColumn("Reason", Width: 2), new ReportColumn("Count", ReportColumnAlignment.Right)],
            data.HumanVerificationRejectionReasons.Select(r => new object?[] { r.Reason, r.Count }));
    }

    private static TableSection Detail(ValidationVerificationData data)
    {
        var rows = new List<object?[]>
        {
            new object?[] { "Summary", "Total uploads", data.TotalUploads },
            new object?[] { "Automatic compliance", "Compliant", data.AutomaticCompliance.Compliant },
            new object?[] { "Automatic compliance", "Rejected", data.AutomaticCompliance.Rejected },
            new object?[] { "Automatic compliance", "Pending", data.AutomaticCompliance.Pending },
            new object?[] { "Human verification", "Verified", data.HumanVerification.Verified },
            new object?[] { "Human verification", "Rejected", data.HumanVerification.Rejected },
            new object?[] { "Human verification", "Unverified", data.HumanVerification.Unverified },
            new object?[] { "Human verification", "Pending", data.HumanVerification.Pending }
        };

        foreach (var item in data.ByDocumentType)
        {
            rows.Add(["Document type", item.Type, item.Count]);
        }

        foreach (var item in data.AutomaticRejectionReasons)
        {
            rows.Add(["Automatic rejection reason", item.Reason, item.Count]);
        }

        foreach (var item in data.HumanVerificationRejectionReasons)
        {
            rows.Add(["Human verification rejection reason", item.Reason, item.Count]);
        }

        return new TableSection(
            "Validation and verification",
            [new ReportColumn("Category"), new ReportColumn("Metric"), new ReportColumn("Count", ReportColumnAlignment.Right)],
            rows);
    }
}
