using Custodian.Shared.Reporting.Models;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Reports;
using Xunit;

namespace Custodian.Workflow.Tests.Unit.Reports;

/// <summary>CSTD-38: the Delivery Readiness &amp; Handoff report model, checked section by section.</summary>
public class ReadinessHandoffReportBuilderTests
{
    private const string TenantId = "tenant-a";
    private const string GeneratedBy = "staff@example.com";
    private static readonly Guid EngagementId = Guid.Parse("abcdef12-3456-7890-abcd-ef1234567890");
    private static readonly DateTimeOffset GeneratedAt = new(2026, 10, 17, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTime EvaluatedAt = new(2026, 10, 17, 9, 29, 0, DateTimeKind.Utc);

    private static readonly string[] SectionTitles =
    [
        "Engagement", "Readiness (generation time)", "Readiness blockers", "Document verification summary",
        "Approval conditions", "Payment conditions", "Handoff (persisted)", "Closure (persisted)", "Definitions"
    ];

    private static ReadinessResponse Readiness(bool isReady, string state = "Ready", params ReadinessBlocker[] blockers) => new()
    {
        EngagementId = EngagementId,
        IsReady = isReady,
        State = state,
        Blockers = blockers.ToList(),
        EvaluatedAtUtc = EvaluatedAt
    };

    private static ReadinessHandoffReportData Data(
        ReadinessResponse? readiness = null,
        bool readinessUnavailable = false,
        IReadOnlyList<ReadinessHandoffDocument>? documents = null,
        bool documentsUnavailable = false,
        IReadOnlyList<ReadinessHandoffCondition>? approvals = null,
        IReadOnlyList<ReadinessHandoffCondition>? payments = null,
        ReadinessHandoffContext? handoff = null,
        ReadinessClosureContext? closure = null) => new(
            GeneratedAt,
            new ReadinessHandoffEngagement(
                EngagementId, TenantId, "client-1", "staff-1", "Closure", "Started",
                new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), null),
            readinessUnavailable ? null : readiness ?? Readiness(true),
            readinessUnavailable,
            documents ?? [],
            documentsUnavailable,
            approvals ?? [],
            payments ?? [],
            handoff,
            closure);

    private static ReadinessHandoffCondition Condition(
        string title,
        string status = "Satisfied",
        bool isActive = true,
        string? approvalStatus = null,
        decimal? amount = null,
        string? currency = null,
        DateTime? satisfiedAt = null,
        string? rejectionReason = null) => new(
            Guid.NewGuid(), "Approval", title, status, approvalStatus, amount, currency, null, null,
            satisfiedAt, null, rejectionReason, isActive);

    private static T Section<T>(ReadinessHandoffReport report, string title) where T : ReportSection =>
        Assert.IsType<T>(Assert.Single(report.Sections, s => s.Title == title));

    private static object? Value(KeyValueSection section, string label) =>
        Assert.Single(section.Pairs, p => p.Label == label).Value;

    private static ReadinessHandoffReport Build(ReadinessHandoffReportData data) =>
        ReadinessHandoffReportBuilder.Build(data, TenantId, GeneratedBy);

    // ---- Metadata and layout ----

    [Fact]
    public void Build_NullData_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ReadinessHandoffReportBuilder.Build(null!, TenantId, GeneratedBy));
    }

    [Fact]
    public void Build_FillsMetadataFromArguments()
    {
        var metadata = Build(Data()).Metadata;

        Assert.Equal("DELIVERY_READINESS_HANDOFF", metadata.ReportCode);
        Assert.Equal("Delivery Readiness & Handoff Summary", metadata.Title);
        Assert.Equal(TenantId, metadata.TenantId);
        Assert.Equal(GeneratedBy, metadata.GeneratedBy);
        Assert.Equal(GeneratedAt, metadata.GeneratedAtUtc);
        Assert.Equal("Workflow service live database", metadata.DataSource);
        Assert.Equal(
            [new("Engagement", "ENG-ABCDEF"), new KeyValuePair<string, string>("Generated at (UTC)", "2026-10-17 09:30:00Z")],
            metadata.AppliedFilters);
    }

    [Fact]
    public void Build_HasEverySectionInOrder()
    {
        Assert.Equal(SectionTitles, Build(Data()).Sections.Select(s => s.Title));
    }

    [Fact]
    public void EngagementCode_IsFirstSixHexCharsUpperCased()
    {
        Assert.Equal("ENG-ABCDEF", ReadinessHandoffReportBuilder.EngagementCode(EngagementId));
    }

    [Fact]
    public void Summary_ShowsEngagementHeaderFields()
    {
        var summary = Section<KeyValueSection>(Build(Data()), "Engagement");

        Assert.Equal(
            ["Engagement", "Client", "Responsible staff", "Stage", "Status", "Created"],
            summary.Pairs.Select(p => p.Label));
        Assert.Equal("ENG-ABCDEF", Value(summary, "Engagement"));
        Assert.Equal("client-1", Value(summary, "Client"));
        Assert.Equal("staff-1", Value(summary, "Responsible staff"));
        Assert.Equal("Closure", Value(summary, "Stage"));
        Assert.Equal("Started", Value(summary, "Status"));
        Assert.Equal("2026-09-01 08:00:00Z", Value(summary, "Created"));
    }

    [Fact]
    public void Definitions_ListsEveryParagraph()
    {
        var definitions = Section<TextSection>(Build(Data()), "Definitions");

        Assert.Equal(6, definitions.Paragraphs.Count);
        Assert.StartsWith("Readiness verdict:", definitions.Paragraphs[0]);
        Assert.Equal("All timestamps are UTC.", definitions.Paragraphs[^1]);
    }

    // ---- Readiness verdict ----

    [Fact]
    public void Verdict_Ready_WhenReadinessSaysReadyAndDocumentsLoaded()
    {
        var verdict = Section<KeyValueSection>(Build(Data(documents: [Document("Passport")])), "Readiness (generation time)");

        Assert.Equal("Ready for handoff and closure", Value(verdict, "Verdict"));
        Assert.Equal("Ready", Value(verdict, "State"));
        Assert.Equal("2026-10-17 09:29:00Z", Value(verdict, "Evaluated at (UTC)"));
        Assert.Equal("1 document(s) on record", Value(verdict, "Documents evidence"));
    }

    [Fact]
    public void Verdict_NotReady_WhenReadinessSaysNotReady()
    {
        var verdict = Section<KeyValueSection>(Build(Data(Readiness(false, "NotReady"))), "Readiness (generation time)");

        Assert.Equal("Not ready", Value(verdict, "Verdict"));
        Assert.Equal("NotReady", Value(verdict, "State"));
    }

    [Fact]
    public void Verdict_NotReady_WhenDocumentsUnavailable_EvenIfReadinessSaysReady()
    {
        var verdict = Section<KeyValueSection>(Build(Data(Readiness(true), documentsUnavailable: true)), "Readiness (generation time)");

        Assert.Equal("Not ready", Value(verdict, "Verdict"));
        Assert.Equal("Ready", Value(verdict, "State"));
        Assert.Equal("Unavailable at generation time", Value(verdict, "Documents evidence"));
    }

    [Fact]
    public void Verdict_ReadinessUnavailable_SaysSoAndHasNoEvaluationTime()
    {
        var verdict = Section<KeyValueSection>(Build(Data(readinessUnavailable: true)), "Readiness (generation time)");

        Assert.Equal("Not available — readiness service could not be reached", Value(verdict, "Verdict"));
        Assert.Equal("Unavailable", Value(verdict, "State"));
        Assert.Equal("n/a", Value(verdict, "Evaluated at (UTC)"));
        Assert.Equal("0 document(s) on record", Value(verdict, "Documents evidence"));
    }

    [Fact]
    public void Verdict_NoReadinessResultButNotFlaggedUnavailable_ShowsUnknownState()
    {
        var verdict = Section<KeyValueSection>(Build(Data() with { Readiness = null }), "Readiness (generation time)");

        Assert.Equal("Not ready", Value(verdict, "Verdict"));
        Assert.Equal("Unknown", Value(verdict, "State"));
        Assert.Equal("n/a", Value(verdict, "Evaluated at (UTC)"));
    }

    // ---- Readiness blockers ----

    [Fact]
    public void Blockers_ReadinessUnavailable_SaysReadinessCouldNotBeEvaluated()
    {
        var blockers = Section<EmptySection>(Build(Data(readinessUnavailable: true)), "Readiness blockers");

        Assert.Equal("Readiness could not be evaluated. The report cannot confirm the engagement is ready.", blockers.Message);
    }

    [Fact]
    public void Blockers_NoneReported_SaysNoBlockers()
    {
        var blockers = Section<EmptySection>(Build(Data(Readiness(true))), "Readiness blockers");

        Assert.Equal("No blockers — the engagement is ready for handoff.", blockers.Message);
    }

    [Fact]
    public void Blockers_Reported_AreListedWithKindTitleAndReason()
    {
        var readiness = Readiness(false, "NotReady",
            new ReadinessBlocker { Kind = "Task", Title = "Open task", Reason = "'Sign-off' is still Pending." },
            new ReadinessBlocker { Kind = "Document", Title = "Passport", Reason = "Not verified." });

        var blockers = Section<TableSection>(Build(Data(readiness)), "Readiness blockers");

        Assert.Equal(["Kind", "Blocker", "Reason"], blockers.Columns.Select(c => c.Header));
        Assert.Equal(
            [
                ["Task", "Open task", "'Sign-off' is still Pending."],
                ["Document", "Passport", "Not verified."]
            ],
            blockers.Rows.Select(r => r.ToArray()));
    }

    // ---- Document verification ----

    private static ReadinessHandoffDocument Document(string type, string? notes = null) =>
        new(Guid.NewGuid(), type, "Compliant", "Verified", "staff-1", null, notes);

    [Fact]
    public void Documents_Unavailable_ExplainsVerdictIsNotUpgraded()
    {
        var documents = Section<EmptySection>(Build(Data(documentsUnavailable: true)), "Document verification summary");

        Assert.Equal(
            "The Documents service was unavailable at generation time. Document verification could not be confirmed and the readiness verdict is not upgraded.",
            documents.Message);
    }

    [Fact]
    public void Documents_None_SaysNoneOnRecord()
    {
        var documents = Section<EmptySection>(Build(Data()), "Document verification summary");

        Assert.Equal("No documents are on record for this engagement.", documents.Message);
    }

    [Fact]
    public void Documents_AreListed_WithEmptyNotesWhenMissing()
    {
        var documents = Section<TableSection>(
            Build(Data(documents: [Document("Passport", "Expires 2030"), Document("Bank statement")])),
            "Document verification summary");

        Assert.Equal(["Type", "Compliance", "Verification", "Notes"], documents.Columns.Select(c => c.Header));
        Assert.Equal(
            [
                ["Passport", "Compliant", "Verified", "Expires 2030"],
                ["Bank statement", "Compliant", "Verified", ""]
            ],
            documents.Rows.Select(r => r.ToArray()));
    }

    // ---- Approval conditions ----

    [Fact]
    public void Approvals_None_SaysNoneConfigured()
    {
        var approvals = Section<EmptySection>(Build(Data()), "Approval conditions");

        Assert.Equal("No approval conditions are configured for this engagement.", approvals.Message);
    }

    [Fact]
    public void Approvals_AreListed_InactiveMarked_MissingValuesShownAsNotApplicable()
    {
        var satisfied = Condition("Partner sign-off", "Satisfied", approvalStatus: "Approved",
            satisfiedAt: new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc));
        var inactive = Condition("Old gate", "Pending", isActive: false, rejectionReason: "Superseded");

        var approvals = Section<TableSection>(Build(Data(approvals: [satisfied, inactive])), "Approval conditions");

        Assert.Equal(
            ["Title", "Status", "Approval status", "Satisfied at (UTC)", "Rejection reason"],
            approvals.Columns.Select(c => c.Header));
        Assert.Equal(
            [
                ["Partner sign-off", "Satisfied", "Approved", "2026-10-05 12:00:00Z", ""],
                ["Old gate", "Pending (inactive)", "n/a", "n/a", "Superseded"]
            ],
            approvals.Rows.Select(r => r.ToArray()));
    }

    // ---- Payment conditions ----

    [Fact]
    public void Payments_None_SaysNoneConfigured()
    {
        var payments = Section<EmptySection>(Build(Data()), "Payment conditions");

        Assert.Equal("No payment conditions are configured for this engagement.", payments.Message);
    }

    [Fact]
    public void Payments_AreListed_WithTwoDecimalAmounts_InactiveMarked()
    {
        var paid = Condition("Retainer", "Satisfied", amount: 2500m, currency: "LKR",
            satisfiedAt: new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        var inactive = Condition("Deposit", "Pending", isActive: false);

        var payments = Section<TableSection>(Build(Data(payments: [paid, inactive])), "Payment conditions");

        Assert.Equal(["Title", "Amount", "Currency", "Status", "Satisfied at (UTC)"], payments.Columns.Select(c => c.Header));
        Assert.Equal(
            [
                ["Retainer", "2500.00", "LKR", "Satisfied", "2026-10-06 00:00:00Z"],
                ["Deposit", "n/a", "n/a", "Pending (inactive)", "n/a"]
            ],
            payments.Rows.Select(r => r.ToArray()));
    }

    // ---- Handoff and closure ----

    [Fact]
    public void Handoff_NotYet_SaysNotHandedOff()
    {
        var handoff = Section<EmptySection>(Build(Data()), "Handoff (persisted)");

        Assert.Equal("The engagement has not been handed off yet.", handoff.Message);
    }

    [Fact]
    public void Handoff_Recorded_ShowsWhoWhenAndSummary()
    {
        var context = new ReadinessHandoffContext("Delivered pack", "staff-1", new DateTime(2026, 10, 10, 15, 0, 0, DateTimeKind.Utc));

        var handoff = Section<KeyValueSection>(Build(Data(handoff: context)), "Handoff (persisted)");

        Assert.Equal("staff-1", Value(handoff, "Handed off by"));
        Assert.Equal("2026-10-10 15:00:00Z", Value(handoff, "Handed off at (UTC)"));
        Assert.Equal("Delivered pack", Value(handoff, "Summary"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Handoff_BlankSummary_ShowsNoneProvided(string summary)
    {
        var context = new ReadinessHandoffContext(summary, "staff-1", DateTime.UtcNow);

        var handoff = Section<KeyValueSection>(Build(Data(handoff: context)), "Handoff (persisted)");

        Assert.Equal("(none provided)", Value(handoff, "Summary"));
    }

    [Fact]
    public void Closure_NotYet_SaysNotClosed()
    {
        var closure = Section<EmptySection>(Build(Data()), "Closure (persisted)");

        Assert.Equal("The engagement has not been closed yet.", closure.Message);
    }

    [Fact]
    public void Closure_Recorded_ShowsWhoWhenAndSummary()
    {
        var context = new ReadinessClosureContext("All done", "owner-1", new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc));

        var closure = Section<KeyValueSection>(Build(Data(closure: context)), "Closure (persisted)");

        Assert.Equal("owner-1", Value(closure, "Closed by"));
        Assert.Equal("2026-10-12 09:00:00Z", Value(closure, "Closed at (UTC)"));
        Assert.Equal("All done", Value(closure, "Summary"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Closure_BlankSummary_ShowsNoneProvided(string? summary)
    {
        var context = new ReadinessClosureContext(summary, "owner-1", DateTime.UtcNow);

        var closure = Section<KeyValueSection>(Build(Data(closure: context)), "Closure (persisted)");

        Assert.Equal("(none provided)", Value(closure, "Summary"));
    }
}
