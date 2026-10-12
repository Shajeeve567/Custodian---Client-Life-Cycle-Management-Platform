using Custodian.Workflow.DTOs;

namespace Custodian.Workflow.Services.Reports;

/// <summary>CSTD-38: the engagement header fields shown at the top of the report.</summary>
public sealed record ReadinessHandoffEngagement(
    Guid EngagementId,
    string TenantId,
    string ClientId,
    string StaffId,
    string Stage,
    string Status,
    DateTime CreatedAt,
    DateTime? ClosedAt);

/// <summary>One document in the "document verification summary" section.</summary>
public sealed record ReadinessHandoffDocument(
    Guid DocumentId,
    string Type,
    string ComplianceStatus,
    string VerificationStatus,
    string? VerifiedBy,
    DateTime? VerifiedAt,
    string? Notes);

/// <summary>One condition (Approval or Payment) in the report.</summary>
public sealed record ReadinessHandoffCondition(
    Guid ConditionId,
    string Type,
    string Title,
    string Status,
    string? ApprovalStatus,
    decimal? Amount,
    string? Currency,
    string? PaymentType,
    DateTime? DueDateUtc,
    DateTime? SatisfiedAt,
    string? SatisfiedBy,
    string? RejectionReason,
    bool IsActive);

/// <summary>Persisted handoff context (CSTD-20), or null when the engagement has not been handed off yet.</summary>
public sealed record ReadinessHandoffContext(
    string Summary,
    string HandedOffBy,
    DateTime HandedOffAtUtc);

/// <summary>Persisted closure context (CSTD-20), or null when the engagement is not closed.</summary>
public sealed record ReadinessClosureContext(
    string? Summary,
    string ClosedBy,
    DateTime ClosedAt);

/// <summary>
/// CSTD-38: everything the report needs. <see cref="Readiness"/> is CSTD-20's verdict — never
/// recomputed here. Availability flags distinguish "no data" from "could not fetch"; the top-line
/// verdict (<see cref="IsReady"/>) is false whenever any required input could not be read, so the
/// report can never claim Ready based on partial evidence.
/// </summary>
public sealed record ReadinessHandoffReportData(
    DateTimeOffset GeneratedAtUtc,
    ReadinessHandoffEngagement Engagement,
    ReadinessResponse? Readiness,
    bool ReadinessUnavailable,
    IReadOnlyList<ReadinessHandoffDocument> Documents,
    bool DocumentsUnavailable,
    IReadOnlyList<ReadinessHandoffCondition> ApprovalConditions,
    IReadOnlyList<ReadinessHandoffCondition> PaymentConditions,
    ReadinessHandoffContext? Handoff,
    ReadinessClosureContext? Closure)
{
    /// <summary>
    /// Top-line verdict at generation time. Ready only when CSTD-20 said Ready AND the report
    /// could load the evidence it needs to justify that. Never Ready when a dependency failed.
    /// </summary>
    public bool IsReady =>
        !ReadinessUnavailable
        && Readiness?.IsReady == true
        && !DocumentsUnavailable;
}