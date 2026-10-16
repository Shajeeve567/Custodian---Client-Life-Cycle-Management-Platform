namespace Custodian.Audit.Services.Reports;

using System.Globalization;
using Custodian.Shared.Reporting.Models;

/// <summary>
/// CSTD-248: Builds deterministic report sections and the CSV detail table for Proof-of-Delivery Report.
/// Reuses the established hash contract and CSTD-247 verification results without inventing new hash algorithms.
/// Formats readable abbreviated hashes for visual presentation while preserving full 64-char hashes for export.
/// </summary>
public static class ProofOfDeliveryReportBuilder
{
    public const string ReportCode = "PROOF_OF_DELIVERY";
    public const string Title = "Proof of Delivery Report";
    public const string DataSource = "Audit service live event ledger and SHA-256 hash chain";

    public const string HashAbbreviationFootnote =
        "Note: Event IDs and hashes in this timeline are abbreviated for readability (hashes display the first 8 hex characters followed by '...'). Complete 64-character SHA-256 hashes remain preserved in the underlying audit records.";

    public static ProofOfDeliveryReport Build(
        ProofOfDeliveryVerifiedReportData data,
        string tenantId,
        string generatedBy)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(generatedBy);

        var generatedAtUtc = data.Verification.VerifiedAtUtc;

        var metadata = new ReportMetadata(
            ReportCode,
            Title,
            tenantId,
            generatedAtUtc,
            generatedBy,
            AppliedFilters(data.Aggregate.Engagement.EngagementId),
            DataSource);

        var sections = new List<ReportSection>
        {
            EngagementSummary(data),
            IntegrityStatus(data),
            EvidenceStatement()
        };

        // Explicit explanations for broken chains and unavailable verification
        var noticeSection = FailureNotice(data);
        if (noticeSection != null)
        {
            sections.Add(noticeSection);
        }

        // Ordered event timeline or empty-history presentation
        sections.Add(Timeline(data));

        return new ProofOfDeliveryReport(metadata, sections, Detail(data));
    }

    public static string AbbreviateHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return "n/a";
        }

        if (hash == "0")
        {
            return "0 (genesis)";
        }

        if (hash == "0000000000000000000000000000000000000000000000000000000000000000")
        {
            return "00000000... (genesis)";
        }

        if (hash.Length <= 12)
        {
            return hash;
        }

        return $"{hash[..8]}...";
    }

    public static string AbbreviateId(Guid id) => $"{id.ToString("N")[..8]}...";

    public static string EngagementCode(Guid engagementId) =>
        $"ENG-{engagementId.ToString("N")[..6].ToUpperInvariant()}";

    private static IEnumerable<KeyValuePair<string, string>> AppliedFilters(Guid engagementId) =>
    [
        new("Engagement", EngagementCode(engagementId)),
        new("Engagement ID", engagementId.ToString())
    ];

    private static ReportSection EngagementSummary(ProofOfDeliveryVerifiedReportData data)
    {
        var eng = data.Aggregate.Engagement;
        var pairs = new List<ReportKeyValue>
        {
            new("Engagement ID", eng.EngagementId.ToString()),
            new("Client ID", eng.ClientId),
            new("Status", eng.Status),
            new("Stage", eng.Stage),
            new("Stage progress", $"{eng.StageProgressPercentage}%"),
            new("Responsible staff", eng.StaffId),
            new($"Created at {ReportTimeZone.ColumnSuffix}", ReportValue.FormatDateTime(eng.CreatedAt)),
            new($"Closed at {ReportTimeZone.ColumnSuffix}", eng.ClosedAt.HasValue
                ? ReportValue.FormatDateTime(eng.ClosedAt.Value)
                : "Open / In progress")
        };

        return new KeyValueSection("Engagement summary", pairs);
    }

    private static ReportSection IntegrityStatus(ProofOfDeliveryVerifiedReportData data)
    {
        var v = data.Verification;
        var pairs = new List<ReportKeyValue>
        {
            new("Integrity status", v.Status.ToString().ToUpperInvariant()),
            new("Audit evidence recorded", v.HasAuditEvidence ? "Yes" : "No"),
            new("Delivery confirmation established", v.HasDeliveryEvidence
                ? "Yes (explicit delivery event verified)"
                : "No (not independently established by audit evidence)"),
            new("Verified event count", v.VerifiedEventCount),
            new("Total events recorded", data.Aggregate.TotalEvents),
            new("Verified sequence range", v.FirstSequenceNumber.HasValue && v.LastSequenceNumber.HasValue
                ? $"{v.FirstSequenceNumber} - {v.LastSequenceNumber}"
                : "None"),
            new("Genesis hash (abbr)", AbbreviateHash(v.GenesisHash)),
            new("Chain tip hash (abbr)", AbbreviateHash(v.StoredChainTipHash)),
            new($"Verified at {ReportTimeZone.ColumnSuffix}", ReportValue.FormatDateTime(v.VerifiedAtUtc)),
            new("Live-head consistency", v.IsConsistentWithLiveHead
                ? "Verified (consistent with live database head)"
                : $"Historical snapshot only (sequence {v.LastSequenceNumber ?? 0}; live head differs/unverified)")
        };

        if (v.Status == ProofOfDeliveryIntegrityStatus.Tampered)
        {
            pairs.Add(new("Broken at event ID", v.BrokenAtEventId.HasValue ? AbbreviateId(v.BrokenAtEventId.Value) : "n/a"));
            pairs.Add(new("Broken at sequence", v.BrokenAtSequenceNumber?.ToString() ?? "n/a"));
            pairs.Add(new("Failure reason", v.FailureReason ?? "Cryptographic verification failed."));
        }
        else if (v.Status == ProofOfDeliveryIntegrityStatus.Unavailable)
        {
            pairs.Add(new("Failure reason", v.FailureReason ?? "Verification unavailable."));
        }

        return new KeyValueSection("Cryptographic integrity & verification", pairs);
    }

    private static ReportSection EvidenceStatement() => new TextSection(
        "Evidence & integrity statement",
        [
            "This report provides mathematical verification of the SHA-256 tamper-evident hash chain recorded for this engagement. A status of VALID confirms that every recorded audit event from genesis to the verified tip possesses an unbroken cryptographic link and matches its recorded payload and timestamp.",
            "IMPORTANT NOTICE: Cryptographic verification confirms the tamper-evident integrity of recorded audit history, not necessarily that final delivery or completion has occurred. Engagement delivery and lifecycle completion are governed by workflow conditions, required deliverables, and authoritative stage transitions. Delivery confirmation is not independently established unless a dedicated delivery-confirmation event is recorded in the ledger."
        ]);

    private static ReportSection? FailureNotice(ProofOfDeliveryVerifiedReportData data)
    {
        var v = data.Verification;
        if (v.Status == ProofOfDeliveryIntegrityStatus.Tampered)
        {
            return new TextSection(
                "Integrity failure notice",
                [
                    v.FailureReason ?? "Cryptographic verification detected an invalid hash or broken link in the audit chain.",
                    "WARNING: The audit history has been flagged as TAMPERED. Subsequent events after the broken link cannot be cryptographically verified."
                ]);
        }

        if (v.Status == ProofOfDeliveryIntegrityStatus.Unavailable)
        {
            return new TextSection(
                "Verification notice",
                [
                    v.FailureReason ?? "Live cryptographic verification was unavailable at report-generation time.",
                    "The report snapshot could not be verified against the cryptographic hash chain due to a dependency or service failure."
                ]);
        }

        if (!v.IsConsistentWithLiveHead && v.Status == ProofOfDeliveryIntegrityStatus.Valid)
        {
            return new TextSection(
                "Live head consistency notice",
                [
                    $"The verified event boundary reflects a historical snapshot through sequence {v.LastSequenceNumber ?? 0}.",
                    "NOTICE: The current live database tip differs from this snapshot. Events appended after this snapshot boundary have not been verified as part of this report."
                ]);
        }

        return null;
    }

    private static ReportSection Timeline(ProofOfDeliveryVerifiedReportData data)
    {
        const string title = "Audit event timeline";

        if (data.Aggregate.IsEmpty || data.Aggregate.Events.Count == 0)
        {
            return new EmptySection(title, "No audit events have been recorded for this engagement. The audit ledger is empty.");
        }

        var columns = new ReportColumn[]
        {
            new("Seq #", ReportColumnAlignment.Right, 0.6f),
            new("Event ID (abbr)", ReportColumnAlignment.Left, 1.1f),
            new($"Timestamp {ReportTimeZone.ColumnSuffix}", ReportColumnAlignment.Left, 1.5f),
            new("Actor", ReportColumnAlignment.Left, 1.4f),
            new("Event Type", ReportColumnAlignment.Left, 2.2f),
            new("Previous Hash (abbr)", ReportColumnAlignment.Left, 1.2f),
            new("Current Hash (abbr)", ReportColumnAlignment.Left, 1.2f)
        };

        var rows = data.Aggregate.Events.Select(e => (IReadOnlyList<object?>)new object?[]
        {
            e.SequenceNumber,
            AbbreviateId(e.EventId),
            ReportValue.FormatDateTime(e.Timestamp),
            e.Actor,
            e.Type,
            AbbreviateHash(e.PreviousHash),
            AbbreviateHash(e.Hash)
        });

        return new TableSection(title, columns, rows, HashAbbreviationFootnote);
    }

    public static TableSection Detail(ProofOfDeliveryVerifiedReportData data)
    {
        var columns = new ReportColumn[]
        {
            new("Sequence", ReportColumnAlignment.Right),
            new("EventId", ReportColumnAlignment.Left),
            new("TimestampUtc", ReportColumnAlignment.Left),
            new("Actor", ReportColumnAlignment.Left),
            new("EventType", ReportColumnAlignment.Left),
            new("PreviousHash", ReportColumnAlignment.Left),
            new("CurrentHash", ReportColumnAlignment.Left)
        };

        var rows = data.Aggregate.Events.Select(e => (IReadOnlyList<object?>)new object?[]
        {
            e.SequenceNumber,
            e.EventId.ToString("D"),
            ReportValue.FormatDateTime(e.Timestamp),
            e.Actor,
            e.Type,
            e.PreviousHash ?? string.Empty,
            e.Hash ?? string.Empty
        });

        return new TableSection("Audit event ledger detail", columns, rows);
    }
}
