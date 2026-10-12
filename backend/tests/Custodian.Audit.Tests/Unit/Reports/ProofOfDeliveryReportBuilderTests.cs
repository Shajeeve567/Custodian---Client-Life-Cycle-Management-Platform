namespace Custodian.Audit.Tests.Unit.Reports;

using System;
using System.Linq;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.Reports;
using Custodian.Shared.Reporting.Models;
using Custodian.Shared.Reporting.Rendering;
using UglyToad.PdfPig;
using Xunit;

public class ProofOfDeliveryReportBuilderTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _engagementId = Guid.NewGuid();
    private readonly DateTime _fixedNow = new(2026, 10, 11, 14, 30, 0, DateTimeKind.Utc);

    private WorkflowEngagementSummary CreateSampleEngagementSummary(
        string status = "InProgress",
        string stage = "Execution",
        DateTime? closedAt = null) => new()
    {
        EngagementId = _engagementId,
        TenantId = _tenantId.ToString(),
        ClientId = "client-custodian-42",
        StaffId = "staff-john-doe",
        Status = status,
        Stage = stage,
        StageProgressPercentage = 80,
        CreatedAt = new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc),
        ClosedAt = closedAt
    };

    private ProofOfDeliveryTimelineEvent CreateTimelineEvent(
        long seq,
        string type,
        string prevHash,
        string hash,
        string actor = "staff-1") => new()
    {
        EventId = Guid.NewGuid(),
        EngagementId = _engagementId,
        TenantId = _tenantId,
        Actor = actor,
        Type = type,
        Timestamp = _fixedNow.AddDays((int)seq),
        Payload = "{\"sensitive\":\"confidential_ssn_123\"}",
        SequenceNumber = seq,
        PreviousHash = prevHash,
        Hash = hash
    };

    [Fact]
    public void Build_ValidHistory_ContainsAllRequiredSectionsAndMetadata()
    {
        var hash1 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var hash2 = "a8b3c4d5e6f70123456789abcdef0123456789abcdef0123456789abcdef0123";
        var hash3 = "f1e2d3c4b5a67890abcdef1234567890abcdef1234567890abcdef1234567890";

        var e1 = CreateTimelineEvent(101, "Genesis", "0", hash1, "system");
        var e2 = CreateTimelineEvent(102, "StageChange", hash1, hash2, "staff-1");
        var e3 = CreateTimelineEvent(103, "DocumentVerified", hash2, hash3, "staff-2");

        var events = new[] { e1, e2, e3 };
        var boundary = ProofOfDeliveryHistoryBoundary.Create(events, _fixedNow);

        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = CreateSampleEngagementSummary(),
                HistoryBoundary = boundary,
                Events = events
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Valid,
                VerifiedEventCount = 3,
                FirstSequenceNumber = 101,
                LastSequenceNumber = 103,
                FirstEventId = e1.EventId,
                LastEventId = e3.EventId,
                FirstEventTimestamp = e1.Timestamp,
                LastEventTimestamp = e3.Timestamp,
                GenesisHash = "0",
                StoredChainTipHash = hash3,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = true,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = null
            }
        };

        var report = ProofOfDeliveryReportBuilder.Build(verifiedData, _tenantId.ToString(), "auditor-1");

        // 1. Metadata checks
        Assert.NotNull(report);
        Assert.Equal(ReportPageOrientation.Landscape, report.Orientation);
        Assert.Equal(ProofOfDeliveryReportBuilder.ReportCode, report.Metadata.ReportCode);
        Assert.Equal(ProofOfDeliveryReportBuilder.Title, report.Metadata.Title);
        Assert.Equal(_tenantId.ToString(), report.Metadata.TenantId);
        Assert.Equal("auditor-1", report.Metadata.GeneratedBy);
        Assert.Equal(ProofOfDeliveryReportBuilder.DataSource, report.Metadata.DataSource);
        Assert.Equal(_fixedNow, report.Metadata.GeneratedAtUtc);

        // 2. Sections check
        Assert.Equal(4, report.Sections.Count); // Summary, Integrity, Evidence Statement, Timeline

        // Section 1: Engagement Summary
        var summarySection = Assert.IsType<KeyValueSection>(report.Sections[0]);
        Assert.Equal("Engagement summary", summarySection.Title);
        Assert.Contains(summarySection.Pairs, p => p.Label == "Engagement ID" && (string?)p.Value == _engagementId.ToString());
        Assert.Contains(summarySection.Pairs, p => p.Label == "Client ID" && (string?)p.Value == "client-custodian-42");
        Assert.Contains(summarySection.Pairs, p => p.Label == "Status" && (string?)p.Value == "InProgress");
        Assert.Contains(summarySection.Pairs, p => p.Label == "Stage" && (string?)p.Value == "Execution");
        Assert.Contains(summarySection.Pairs, p => p.Label == "Stage progress" && (string?)p.Value == "80%");
        Assert.Contains(summarySection.Pairs, p => p.Label == "Responsible staff" && (string?)p.Value == "staff-john-doe");

        // Section 2: Integrity & Verification
        var integritySection = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.Equal("Cryptographic integrity & verification", integritySection.Title);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Integrity status" && (string?)p.Value == "VALID");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Audit evidence recorded" && (string?)p.Value == "Yes");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Delivery confirmation established" && (string?)p.Value == "No (not independently established by audit evidence)");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Verified event count" && (int)p.Value! == 3);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Verified sequence range" && (string?)p.Value == "101 - 103");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Genesis hash (abbr)" && (string?)p.Value == "0 (genesis)");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Chain tip hash (abbr)" && (string?)p.Value == $"{hash3[..8]}...");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Live-head consistency" && (string?)p.Value == "Verified (consistent with live database head)");

        // Section 3: Evidence & Integrity Statement
        var statementSection = Assert.IsType<TextSection>(report.Sections[2]);
        Assert.Equal("Evidence & integrity statement", statementSection.Title);
        Assert.Equal(2, statementSection.Paragraphs.Count);
        Assert.Contains("mathematical verification of the SHA-256", statementSection.Paragraphs[0]);
        Assert.Contains("not necessarily that final delivery or completion has occurred", statementSection.Paragraphs[1]);

        // Section 4: Timeline Table
        var timelineSection = Assert.IsType<TableSection>(report.Sections[3]);
        Assert.Equal("Audit event timeline", timelineSection.Title);
        Assert.Equal(7, timelineSection.Columns.Count);
        Assert.Equal(3, timelineSection.Rows.Count);
        Assert.Equal(ProofOfDeliveryReportBuilder.HashAbbreviationFootnote, timelineSection.Footnote);

        // Check timeline row abbreviations
        var row1 = timelineSection.Rows[0];
        Assert.Equal(101L, row1[0]);
        Assert.Equal(ProofOfDeliveryReportBuilder.AbbreviateId(e1.EventId), row1[1]);
        Assert.Equal("Genesis", row1[4]);
        Assert.Equal("0 (genesis)", row1[5]);
        Assert.Equal($"{hash1[..8]}...", row1[6]);

        // Detail Table (CSV export) preserves full 64-character hashes and raw GUIDs
        Assert.NotNull(report.Detail);
        Assert.Equal("Audit event ledger detail", report.Detail.Title);
        Assert.Equal(7, report.Detail.Columns.Count);
        Assert.Equal(3, report.Detail.Rows.Count);

        var detailRow1 = report.Detail.Rows[0];
        Assert.Equal(101L, detailRow1[0]);
        Assert.Equal(e1.EventId.ToString("D"), detailRow1[1]);
        Assert.Equal("Genesis", detailRow1[4]);
        Assert.Equal("0", detailRow1[5]);
        Assert.Equal(hash1, detailRow1[6]); // Full 64-char hash
    }

    [Fact]
    public void Build_ValidHistoryWithDeliveryEvidence_IntegritySectionReportsDeliveryConfirmed()
    {
        var hash1 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var e1 = CreateTimelineEvent(1, "ProofOfDelivery", "0", hash1);

        var events = new[] { e1 };
        var boundary = ProofOfDeliveryHistoryBoundary.Create(events, _fixedNow);

        var closedEngagement = CreateSampleEngagementSummary("Closed", "Closure", _fixedNow);

        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = closedEngagement,
                HistoryBoundary = boundary,
                Events = events
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Valid,
                VerifiedEventCount = 1,
                FirstSequenceNumber = 1,
                LastSequenceNumber = 1,
                FirstEventId = e1.EventId,
                LastEventId = e1.EventId,
                FirstEventTimestamp = e1.Timestamp,
                LastEventTimestamp = e1.Timestamp,
                GenesisHash = "0",
                StoredChainTipHash = hash1,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = true,
                HasDeliveryEvidence = true,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = null
            }
        };

        var report = ProofOfDeliveryReportBuilder.Build(verifiedData, _tenantId.ToString(), "auditor-1");

        var integritySection = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Delivery confirmation established" && (string?)p.Value == "Yes (explicit delivery event verified)");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Audit evidence recorded" && (string?)p.Value == "Yes");
    }

    [Fact]
    public void Build_EmptyHistory_PresentsEmptySectionWithoutInventingEvents()
    {
        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = CreateSampleEngagementSummary(),
                HistoryBoundary = ProofOfDeliveryHistoryBoundary.Empty(_fixedNow),
                Events = Array.Empty<ProofOfDeliveryTimelineEvent>()
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Empty,
                VerifiedEventCount = 0,
                FirstSequenceNumber = null,
                LastSequenceNumber = null,
                FirstEventId = null,
                LastEventId = null,
                FirstEventTimestamp = null,
                LastEventTimestamp = null,
                GenesisHash = "0",
                StoredChainTipHash = null,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = true,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = null
            }
        };

        var report = ProofOfDeliveryReportBuilder.Build(verifiedData, _tenantId.ToString(), "auditor-1");

        // Integrity section
        var integritySection = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Integrity status" && (string?)p.Value == "EMPTY");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Audit evidence recorded" && (string?)p.Value == "No");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Delivery confirmation established" && (string?)p.Value == "No (not independently established by audit evidence)");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Verified event count" && (int)p.Value! == 0);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Verified sequence range" && (string?)p.Value == "None");

        // Timeline section is EmptySection without invented events
        var emptySection = Assert.IsType<EmptySection>(report.Sections[3]);
        Assert.Equal("Audit event timeline", emptySection.Title);
        Assert.Equal("No audit events have been recorded for this engagement. The audit ledger is empty.", emptySection.Message);

        // Detail table has 0 rows
        Assert.Empty(report.Detail.Rows);
    }

    [Fact]
    public void Build_TamperedChain_ContainsFailureNoticeAndBrokenEventDetails()
    {
        var hash1 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var e1 = CreateTimelineEvent(101, "Genesis", "0", hash1);
        var brokenEventId = Guid.NewGuid();

        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = CreateSampleEngagementSummary(),
                HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[] { e1 }, _fixedNow),
                Events = new[] { e1 }
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Tampered,
                VerifiedEventCount = 1,
                FirstSequenceNumber = 101,
                LastSequenceNumber = 101,
                FirstEventId = e1.EventId,
                LastEventId = e1.EventId,
                FirstEventTimestamp = e1.Timestamp,
                LastEventTimestamp = e1.Timestamp,
                GenesisHash = "0",
                StoredChainTipHash = hash1,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = false,
                HasDeliveryEvidence = false,
                BrokenAtEventId = brokenEventId,
                BrokenAtSequenceNumber = 102,
                FailureReason = "Stored hash does not match recomputed SHA-256 hash at sequence 102."
            }
        };

        var report = ProofOfDeliveryReportBuilder.Build(verifiedData, _tenantId.ToString(), "auditor-1");

        // Integrity section contains broken event specifics
        var integritySection = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Integrity status" && (string?)p.Value == "TAMPERED");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Broken at event ID" && (string?)p.Value == ProofOfDeliveryReportBuilder.AbbreviateId(brokenEventId));
        Assert.Contains(integritySection.Pairs, p => p.Label == "Broken at sequence" && (string?)p.Value == "102");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Failure reason" && ((string?)p.Value)!.Contains("recomputed SHA-256 hash"));

        // Explanatory Failure Notice section exists
        var failureNotice = Assert.IsType<TextSection>(report.Sections[3]);
        Assert.Equal("Integrity failure notice", failureNotice.Title);
        Assert.Contains("TAMPERED", failureNotice.Paragraphs[1]);
    }

    [Fact]
    public void Build_UnavailableVerification_ContainsFailureNotice()
    {
        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = CreateSampleEngagementSummary(),
                HistoryBoundary = ProofOfDeliveryHistoryBoundary.Empty(_fixedNow),
                Events = Array.Empty<ProofOfDeliveryTimelineEvent>()
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Unavailable,
                VerifiedEventCount = 0,
                FirstSequenceNumber = null,
                LastSequenceNumber = null,
                FirstEventId = null,
                LastEventId = null,
                FirstEventTimestamp = null,
                LastEventTimestamp = null,
                GenesisHash = "0",
                StoredChainTipHash = null,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = false,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = "Verification unavailable: Database timeout"
            }
        };

        var report = ProofOfDeliveryReportBuilder.Build(verifiedData, _tenantId.ToString(), "auditor-1");

        var integritySection = Assert.IsType<KeyValueSection>(report.Sections[1]);
        Assert.Contains(integritySection.Pairs, p => p.Label == "Integrity status" && (string?)p.Value == "UNAVAILABLE");
        Assert.Contains(integritySection.Pairs, p => p.Label == "Failure reason" && ((string?)p.Value)!.Contains("Database timeout"));

        var noticeSection = Assert.IsType<TextSection>(report.Sections[3]);
        Assert.Equal("Verification notice", noticeSection.Title);
        Assert.Contains("Database timeout", noticeSection.Paragraphs[0]);
    }

    [Theory]
    [InlineData(null, "n/a")]
    [InlineData("", "n/a")]
    [InlineData("   ", "n/a")]
    [InlineData("0", "0 (genesis)")]
    [InlineData("short", "short")]
    [InlineData("123456789012", "123456789012")]
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "e3b0c442...")]
    public void AbbreviateHash_VariousInputs_FormatsExpectedExcerpts(string? input, string expected)
    {
        var actual = ProofOfDeliveryReportBuilder.AbbreviateHash(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DetailTable_PreservesFullHashesAndOmitsSensitivePayload()
    {
        var fullHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var e = CreateTimelineEvent(1, "TestEvent", "0", fullHash);

        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = CreateSampleEngagementSummary(),
                HistoryBoundary = ProofOfDeliveryHistoryBoundary.Create(new[] { e }, _fixedNow),
                Events = new[] { e }
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Valid,
                VerifiedEventCount = 1,
                FirstSequenceNumber = 1,
                LastSequenceNumber = 1,
                FirstEventId = e.EventId,
                LastEventId = e.EventId,
                FirstEventTimestamp = e.Timestamp,
                LastEventTimestamp = e.Timestamp,
                GenesisHash = "0",
                StoredChainTipHash = fullHash,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = true,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = null
            }
        };

        var detail = ProofOfDeliveryReportBuilder.Detail(verifiedData);

        Assert.Single(detail.Rows);
        var row = detail.Rows[0];

        // Ensure sensitive payload is NOT present in any column
        Assert.DoesNotContain(row, cell => cell?.ToString()?.Contains("confidential_ssn_123") == true);

        // Ensure full 64-char hash is present in CurrentHash column
        Assert.Equal(fullHash, row[6]);
        Assert.Equal("0", row[5]);
    }

    [Fact]
    public void Build_SetsLandscapeOrientation_AndRendersLandscapePdf()
    {
        var hash1 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var e1 = CreateTimelineEvent(1, "Genesis", "0", hash1, "system");
        var events = new[] { e1 };
        var boundary = ProofOfDeliveryHistoryBoundary.Create(events, _fixedNow);

        var verifiedData = new ProofOfDeliveryVerifiedReportData
        {
            Aggregate = new ProofOfDeliveryAggregateData
            {
                Engagement = CreateSampleEngagementSummary(),
                HistoryBoundary = boundary,
                Events = events
            },
            Verification = new ProofOfDeliveryVerificationResult
            {
                Status = ProofOfDeliveryIntegrityStatus.Valid,
                VerifiedEventCount = 1,
                FirstSequenceNumber = 1,
                LastSequenceNumber = 1,
                FirstEventId = e1.EventId,
                LastEventId = e1.EventId,
                FirstEventTimestamp = e1.Timestamp,
                LastEventTimestamp = e1.Timestamp,
                GenesisHash = "0",
                StoredChainTipHash = hash1,
                VerifiedAtUtc = _fixedNow,
                IsConsistentWithLiveHead = true,
                HasDeliveryEvidence = false,
                BrokenAtEventId = null,
                BrokenAtSequenceNumber = null,
                FailureReason = null
            }
        };

        var report = ProofOfDeliveryReportBuilder.Build(verifiedData, _tenantId.ToString(), "auditor-1");

        // Verify Landscape orientation setting
        Assert.Equal(ReportPageOrientation.Landscape, report.Orientation);

        // Verify PDF rendering dimensions in landscape (A4 landscape: 841.89 pt width x 595.28 pt height)
        var renderer = new PdfReportRenderer();
        var pdfBytes = renderer.RenderPdf(report);
        Assert.NotEmpty(pdfBytes);

        using var doc = PdfDocument.Open(pdfBytes);
        Assert.True(doc.NumberOfPages >= 1);
        foreach (var page in doc.GetPages())
        {
            Assert.True(page.Width > page.Height, $"Expected landscape orientation (width > height), but was {page.Width} x {page.Height}");
        }
    }

    [Fact]
    public void ReportModel_DefaultOrientation_IsPortrait()
    {
        var metadata = new ReportMetadata("TEST", "Test", "t1", _fixedNow, "actor", [], "src");
        var report = new SamplePortraitReport(metadata, [new EmptySection("Empty")]);
        Assert.Equal(ReportPageOrientation.Portrait, report.Orientation);
    }

    private sealed class SamplePortraitReport(ReportMetadata metadata, IEnumerable<ReportSection> sections)
        : ReportModel(metadata, sections);
}
