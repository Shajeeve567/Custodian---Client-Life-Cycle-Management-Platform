using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Custodian.Shared.Reporting.Models;

/// <summary>
/// Who a report is printed for (CSTD-14). Internal reports show the tenant in the header; a report a
/// client downloads must not, because tenant ids are internal identifiers.
/// </summary>
public enum ReportAudience
{
    /// <summary>Owner/Staff reports (the default): header shows the tenant and "Generated … by".</summary>
    Internal,

    /// <summary>Client-facing reports: no tenant in the header; "Prepared for" the requester instead.</summary>
    Client
}

/// <summary>
/// CSTD-36-1: who/what/when of a generated report, printed in its header and used for the file name.
/// Built by the owning service from the request (tenant and actor from the JWT, "now" from its
/// TimeProvider), never from report data.
/// </summary>
public sealed partial class ReportMetadata
{
    public ReportMetadata(
        string reportCode,
        string title,
        string tenantId,
        DateTimeOffset generatedAtUtc,
        string generatedBy,
        IEnumerable<KeyValuePair<string, string>> appliedFilters,
        string dataSource,
        string? tenantDisplayName = null,
        ReportAudience audience = ReportAudience.Internal)
    {
        if (string.IsNullOrWhiteSpace(reportCode) || !ReportCodePattern().IsMatch(reportCode))
        {
            throw new ArgumentException(
                "ReportCode must be upper-case letters, digits and underscores, starting with a letter (e.g. SLA_PERFORMANCE).",
                nameof(reportCode));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);
        ArgumentNullException.ThrowIfNull(appliedFilters);

        ReportCode = reportCode;
        Title = title;
        TenantId = tenantId;
        TenantDisplayName = string.IsNullOrWhiteSpace(tenantDisplayName) ? null : tenantDisplayName;
        GeneratedAtUtc = generatedAtUtc.ToUniversalTime();
        GeneratedBy = generatedBy;
        DataSource = dataSource;
        Audience = audience;

        // Kept in the order the service supplied them, so the filters block reads the same way every time.
        var filters = new OrderedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in appliedFilters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(appliedFilters));
            filters[name] = value ?? string.Empty;
        }
        AppliedFilters = new ReadOnlyDictionary<string, string>(filters);
    }

    /// <summary>Stable code, e.g. <c>SLA_PERFORMANCE</c>. Lower-cased in the download file name.</summary>
    public string ReportCode { get; }

    public string Title { get; }

    public string TenantId { get; }

    public string? TenantDisplayName { get; }

    /// <summary>The single "now" the report was computed at, in UTC.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Actor id (or email) of the requester, from the JWT.</summary>
    public string GeneratedBy { get; }

    /// <summary>Filter label → display value, in the order they should be printed.</summary>
    public IReadOnlyDictionary<string, string> AppliedFilters { get; }

    /// <summary>Where the numbers came from, e.g. "Workflow service live database".</summary>
    public string DataSource { get; }

    /// <summary>Internal unless the report is downloaded by a client; see <see cref="ReportAudience"/>.</summary>
    public ReportAudience Audience { get; }

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex ReportCodePattern();
}
