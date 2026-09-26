using System.Text.Json;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Custodian.Workflow.Services.Gates;
using Custodian.Workflow.Services.Sla;

namespace Custodian.Workflow.Services.NextAction;

/// <summary>
/// Pure deterministic next-action rule engine (CSTD-19).
/// Contains zero I/O or state mutations so it can be table-tested thoroughly.
/// </summary>
public static class NextActionRules
{
    private const string StaffReviewBlockerReason = "Your submission is being reviewed by the Custodian team.";
    private const string DocumentUnavailableReason = "Document status temporarily unavailable";
    private const string ConditionUnavailableReason = "Condition status temporarily unavailable";

    // Action types whose completion requires the client to upload evidence (rank 4 when pending).
    private static readonly HashSet<string> EvidenceActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ClientActionType.DocumentUpload,
        ClientActionType.KycDocument,
        ClientActionType.SignAgreement,
        ClientActionType.ProofOfAddress
    };

    private enum LinkedDocumentState
    {
        None,
        Verified,
        ComplianceRejected,
        CompliantUnverified
    }

    public static NextActionResult Decide(NextActionInputs inputs, NextActionView view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Engagement);

        var engagement = inputs.Engagement;

        // Rank 0: Closed / Cancelled
        if (engagement.Status is EngagementStatus.Closed or EngagementStatus.Cancelled)
        {
            return new NextActionResult
            {
                EngagementId = engagement.EngagementId,
                EngagementStatus = engagement.Status.ToString(),
                CurrentStage = engagement.Stage.ToString(),
                OverallState = OverallState.Closed,
                PrimaryAction = null,
                Blockers = Array.Empty<NextActionItem>(),
                NextStageGate = null,
                IsStalled = inputs.IsStalled,
                EvaluatedAtUtc = now
            };
        }

        // Rank 0: Draft -> staff primary "Activate engagement", client no primary
        if (engagement.Status is EngagementStatus.Draft)
        {
            NextActionItem? draftPrimary = null;
            if (view == NextActionView.Staff)
            {
                draftPrimary = new NextActionItem
                {
                    Kind = NextActionKind.StaffTask,
                    ResponsibleParty = ResponsibleParty.Staff,
                    Title = "Activate engagement",
                    Reason = "Engagement is in draft and must be activated to begin work.",
                    StageNumber = (int)engagement.Stage + 1,
                    PriorityRank = 0
                };
            }

            return new NextActionResult
            {
                EngagementId = engagement.EngagementId,
                EngagementStatus = engagement.Status.ToString(),
                CurrentStage = engagement.Stage.ToString(),
                OverallState = OverallState.NotStarted,
                PrimaryAction = draftPrimary,
                Blockers = Array.Empty<NextActionItem>(),
                NextStageGate = null,
                IsStalled = inputs.IsStalled,
                EvaluatedAtUtc = now
            };
        }

        var currentStage = engagement.Stage;
        var currentStageNumber = (int)currentStage + 1;
        var hasNextStage = currentStage < EngagementStage.Closure;
        var nextStage = hasNextStage ? (EngagementStage)((int)currentStage + 1) : (EngagementStage?)null;
        var isExternalUnavailable = inputs.IsDocumentsUnavailable || inputs.IsConditionsUnavailable;

        var candidates = new List<CandidateItem>();

        // Source-owned items keyed by source id, so their mirrored ClientAction can be attached
        // instead of being emitted a second time (AC6: no duplicates).
        var requirementCandidates = new Dictionary<Guid, CandidateItem>();
        var conditionCandidates = new Dictionary<Guid, CandidateItem>();

        // -------------------------------------------------------------
        // 1. Process Requirements (Ranks 1, 3, 8, 10)
        // -------------------------------------------------------------
        if (inputs.Requirements != null)
        {
            foreach (var req in inputs.Requirements)
            {
                if (req.StageNumber.HasValue && req.StageNumber.Value > currentStageNumber)
                {
                    continue; // Later stage requirement
                }

                if (string.Equals(req.Status, RequirementStatus.Approved, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Approved requirements are never blockers
                }

                var sla = GetRequirementSla(req, inputs, now);
                var stageNumber = req.StageNumber ?? currentStageNumber;
                CandidateItem candidate;

                if (string.Equals(req.Status, RequirementStatus.Submitted, StringComparison.OrdinalIgnoreCase))
                {
                    // Staff review
                    candidate = new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.RequirementReview,
                            ResponsibleParty = ResponsibleParty.Staff,
                            Title = $"Review requirement: {req.Type}",
                            Reason = sla.IsOverdue
                                ? $"Submitted requirement '{req.Type}' is awaiting review (Overdue)."
                                : $"Submitted requirement '{req.Type}' is awaiting staff review.",
                            SourceType = ClientActionSourceType.Requirement,
                            SourceId = req.RequirementId,
                            StageNumber = stageNumber,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = sla.IsOverdue ? 8 : 10
                        },
                        IsRejected = false,
                        IsCurrentStage = stageNumber == currentStageNumber,
                        CreatedAt = req.CreatedAt
                    };
                }
                else
                {
                    // Requested or Rejected requirement (Client submission)
                    var isRejected = string.Equals(req.Status, RequirementStatus.Rejected, StringComparison.OrdinalIgnoreCase);
                    var reason = isRejected
                        ? (!string.IsNullOrWhiteSpace(req.RejectionReason) ? $"Requirement rejected: {req.RejectionReason}" : $"Requirement '{req.Type}' was rejected and requires resubmission.")
                        : $"Information '{req.Type}' is required.";

                    candidate = new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.RequirementSubmission,
                            ResponsibleParty = ResponsibleParty.Client,
                            Title = $"Submit requirement: {req.Type}",
                            Reason = reason,
                            SourceType = ClientActionSourceType.Requirement,
                            SourceId = req.RequirementId,
                            StageNumber = stageNumber,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = sla.IsOverdue ? 1 : 3
                        },
                        IsRejected = isRejected,
                        IsCurrentStage = stageNumber == currentStageNumber,
                        CreatedAt = req.CreatedAt
                    };
                }

                candidates.Add(candidate);
                requirementCandidates[req.RequirementId] = candidate;
            }
        }

        // -------------------------------------------------------------
        // 2. Process Active Conditions (Ranks 1, 5, 6) — AC2/AC3/AC4
        // -------------------------------------------------------------
        if (inputs.ActiveConditions != null)
        {
            foreach (var cond in inputs.ActiveConditions)
            {
                // AC2/AC3: Inactive conditions are never read
                if (!cond.IsActive)
                {
                    continue;
                }

                // AC4: Satisfied conditions are never blockers
                if (string.Equals(cond.Status, ConditionStatus.Satisfied, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Only conditions gating the immediate next stage affect current-stage progression
                if (nextStage == null || cond.RequiredBeforeStage != nextStage.Value)
                {
                    continue;
                }

                var sla = GetConditionSla(cond, inputs.SlaStatuses, now);
                CandidateItem candidate;

                if (string.Equals(cond.Type, ConditionType.Approval, StringComparison.OrdinalIgnoreCase))
                {
                    var isRejected = string.Equals(cond.Status, ConditionStatus.Rejected, StringComparison.OrdinalIgnoreCase);

                    // The internal note is staff-only and must never be used as a reason. A client-safe
                    // decline reason is added by CSTD-25; until then the reason is generic.
                    var reason = isRejected
                        ? "Approval was declined."
                        : $"Approval required before advancing to {nextStage.Value}.";

                    candidate = new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.ConditionApproval,
                            ResponsibleParty = ResponsibleParty.Client,
                            Title = cond.Title,
                            Reason = reason,
                            SourceType = ClientActionSourceType.Condition,
                            SourceId = cond.ConditionId,
                            StageNumber = (int)cond.RequiredBeforeStage,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = sla.IsOverdue ? 1 : 5
                        },
                        IsRejected = isRejected,
                        IsCurrentStage = true,
                        CreatedAt = cond.CreatedAt
                    };
                }
                else if (string.Equals(cond.Type, ConditionType.Payment, StringComparison.OrdinalIgnoreCase))
                {
                    var title = view == NextActionView.Staff ? $"Confirm payment received: {cond.Title}" : "Payment due";
                    var reason = view == NextActionView.Staff
                        ? "Confirm payment received."
                        : $"Payment of {cond.Amount:F2} {cond.Currency} is required before advancing to {nextStage.Value}.";

                    candidate = new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.ConditionPayment,
                            ResponsibleParty = ResponsibleParty.Client,
                            Title = title,
                            Reason = reason,
                            SourceType = ClientActionSourceType.Condition,
                            SourceId = cond.ConditionId,
                            StageNumber = (int)cond.RequiredBeforeStage,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = sla.IsOverdue ? 1 : 6
                        },
                        IsRejected = false,
                        IsCurrentStage = true,
                        CreatedAt = cond.CreatedAt
                    };
                }
                else
                {
                    continue;
                }

                candidates.Add(candidate);
                conditionCandidates[cond.ConditionId] = candidate;
            }
        }

        // -------------------------------------------------------------
        // 3. Process ClientActions (Ranks 1, 2, 4, 7, 8, 9, 11)
        // -------------------------------------------------------------
        if (inputs.Actions != null)
        {
            foreach (var act in inputs.Actions)
            {
                // Completed or Cancelled actions are never included
                if (act.Status is ClientActionStatus.Completed or ClientActionStatus.Cancelled)
                {
                    continue;
                }

                // Internal-only actions are never in client view
                if (view == NextActionView.Client && act.IsInternalOnly)
                {
                    continue;
                }

                // Requirement-mirrored actions are represented by their requirement (section 1).
                if (act.LinkedRequirementId.HasValue)
                {
                    if (requirementCandidates.TryGetValue(act.LinkedRequirementId.Value, out var reqCandidate))
                    {
                        AttachActionId(reqCandidate, act.ActionId);
                    }
                    continue;
                }

                // Condition-linked actions are represented by their condition (section 2). A satisfied,
                // inactive or later-stage condition therefore never blocks through its action (AC4).
                if (act.LinkedConditionId.HasValue ||
                    string.Equals(act.SourceType, ClientActionSourceType.Condition, StringComparison.OrdinalIgnoreCase))
                {
                    if (act.LinkedConditionId.HasValue &&
                        conditionCandidates.TryGetValue(act.LinkedConditionId.Value, out var condCandidate))
                    {
                        AttachActionId(condCandidate, act.ActionId);
                    }
                    continue;
                }

                // Only current stage or earlier leftover actions
                if (act.StageNumber > currentStageNumber)
                {
                    continue;
                }

                var docState = GetLinkedDocumentState(act, inputs.Documents);
                if (docState == LinkedDocumentState.Verified)
                {
                    continue; // Verified document is not a blocker
                }

                var sla = GetActionSla(act, inputs.SlaStatuses, now);
                var candidate = ClassifyAction(act, docState, sla, currentStageNumber);
                if (candidate != null)
                {
                    candidates.Add(candidate);
                }
            }
        }

        // -------------------------------------------------------------
        // 4. Sort Candidates using Deterministic Tie-Breakers
        // -------------------------------------------------------------
        // Tie-breakers inside a rank: DueAtUtc ascending (nulls last), then StageNumber, then CreatedAt, then ActionId.
        // In Rank 1: rejected before pending.
        var sortedCandidates = candidates
            .OrderBy(c => c.Item.PriorityRank)
            .ThenByDescending(c => c.IsRejected)
            .ThenBy(c => c.Item.DueAtUtc.HasValue ? 0 : 1)
            .ThenBy(c => c.Item.DueAtUtc)
            .ThenByDescending(c => c.IsCurrentStage)
            .ThenBy(c => c.Item.StageNumber ?? int.MaxValue)
            .ThenBy(c => c.CreatedAt)
            .ThenBy(c => c.Item.ActionId.HasValue ? 0 : 1)
            .ThenBy(c => c.Item.ActionId ?? Guid.Empty)
            .ToList();

        // -------------------------------------------------------------
        // 5. Evaluate Ranks 12, 13, 14 or Assemble Output
        // -------------------------------------------------------------
        NextActionItem? primaryAction = null;
        var blockers = new List<NextActionItem>();
        string overallState;

        if (sortedCandidates.Count == 0)
        {
            if (currentStage == EngagementStage.Closure)
            {
                // Rank 14: Stage == Closure and nothing open -> AllComplete
                overallState = OverallState.AllComplete;
                primaryAction = null;
            }
            else if (isExternalUnavailable)
            {
                // Dependency failure: cannot claim ReadyToAdvance
                overallState = OverallState.BlockedExternal;
                primaryAction = null;
                AddUnavailableBlockers(blockers, inputs);
            }
            else if (inputs.NextStageGate != null && inputs.NextStageGate.IsSatisfied)
            {
                // Rank 12: Gate satisfied -> AdvanceStage
                overallState = OverallState.ReadyToAdvance;
                if (view == NextActionView.Staff)
                {
                    primaryAction = new NextActionItem
                    {
                        Kind = NextActionKind.AdvanceStage,
                        ResponsibleParty = ResponsibleParty.Staff,
                        Title = $"Advance to {nextStage}",
                        Reason = $"All requirements and gates for {currentStage} are satisfied.",
                        StageNumber = currentStageNumber,
                        PriorityRank = 12
                    };
                }
                else
                {
                    primaryAction = null;
                }
            }
            else
            {
                // Rank 13: Gate NOT satisfied
                var gateReason = inputs.NextStageGate?.Reason ?? "Next stage gate requirements are not satisfied.";
                overallState = OverallState.AwaitingStaff;
                if (view == NextActionView.Staff)
                {
                    primaryAction = new NextActionItem
                    {
                        Kind = NextActionKind.StaffTask,
                        ResponsibleParty = ResponsibleParty.Staff,
                        Title = $"Resolve gate blocker: {gateReason}",
                        Reason = gateReason,
                        StageNumber = currentStageNumber,
                        PriorityRank = 13
                    };
                }
                else
                {
                    primaryAction = null;
                    blockers.Add(CreateClientSafeStaffReviewBlocker());
                }
            }
        }
        else
        {
            // Candidates exist in ranks 1-11
            if (view == NextActionView.Staff)
            {
                primaryAction = sortedCandidates[0].Item;
                blockers.AddRange(sortedCandidates.Skip(1).Select(c => c.Item));

                if (isExternalUnavailable)
                {
                    AddUnavailableBlockers(blockers, inputs);
                    overallState = OverallState.BlockedExternal;
                }
                else
                {
                    overallState = primaryAction.ResponsibleParty == ResponsibleParty.Client
                        ? OverallState.ClientActionRequired
                        : OverallState.AwaitingStaff;
                }
            }
            else
            {
                // Client view: primary must have ResponsibleParty == Client
                var clientCandidates = sortedCandidates.Where(c => c.Item.ResponsibleParty == ResponsibleParty.Client).ToList();
                var hasStaffWork = sortedCandidates.Any(c => c.Item.ResponsibleParty == ResponsibleParty.Staff)
                                   || (inputs.NextStageGate != null && !inputs.NextStageGate.IsSatisfied);

                if (clientCandidates.Count > 0)
                {
                    primaryAction = SanitizeClientItem(clientCandidates[0].Item);
                    blockers.AddRange(clientCandidates.Skip(1).Select(c => SanitizeClientItem(c.Item)));

                    if (hasStaffWork)
                    {
                        blockers.Add(CreateClientSafeStaffReviewBlocker());
                    }

                    if (isExternalUnavailable)
                    {
                        AddUnavailableBlockers(blockers, inputs);
                        overallState = OverallState.BlockedExternal;
                    }
                    else
                    {
                        overallState = OverallState.ClientActionRequired;
                    }
                }
                else
                {
                    primaryAction = null;
                    if (hasStaffWork)
                    {
                        blockers.Add(CreateClientSafeStaffReviewBlocker());
                    }

                    if (isExternalUnavailable)
                    {
                        AddUnavailableBlockers(blockers, inputs);
                        overallState = OverallState.BlockedExternal;
                    }
                    else
                    {
                        overallState = OverallState.AwaitingStaff;
                    }
                }
            }
        }

        // Gate Summary (Staff view only)
        GateSummary? gateSummary = null;
        if (view == NextActionView.Staff && inputs.NextStageGate != null && hasNextStage)
        {
            gateSummary = new GateSummary
            {
                TargetStage = nextStage!.Value.ToString(),
                IsSatisfied = inputs.NextStageGate.IsSatisfied,
                Reasons = inputs.NextStageGate.Requirements
                    .Where(r => !r.IsSatisfied && !string.IsNullOrWhiteSpace(r.Reason))
                    .Select(r => r.Reason!)
                    .ToList()
            };
        }

        return new NextActionResult
        {
            EngagementId = engagement.EngagementId,
            EngagementStatus = engagement.Status.ToString(),
            CurrentStage = engagement.Stage.ToString(),
            OverallState = overallState,
            PrimaryAction = primaryAction,
            Blockers = blockers,
            NextStageGate = gateSummary,
            IsStalled = inputs.IsStalled,
            EvaluatedAtUtc = now
        };
    }

    /// <summary>
    /// Maps one open ClientAction to its rank. The responsible party follows the action's state,
    /// not only its AssignedToRole: evidence a client has uploaded is waiting on staff (rank 9/8),
    /// and rejected evidence is back with the client (rank 2/1).
    /// </summary>
    private static CandidateItem? ClassifyAction(ClientAction act, LinkedDocumentState docState, SlaStatus sla, int currentStageNumber)
    {
        var needsResubmission = act.Status == ClientActionStatus.Rejected || docState == LinkedDocumentState.ComplianceRejected;
        if (needsResubmission)
        {
            // Rank 2: Rejected document/evidence (Rank 1 when overdue)
            var reason = ExtractRejectionReason(act.SourceMetadata)
                         ?? (!string.IsNullOrWhiteSpace(act.Description) ? act.Description : null)
                         ?? (sla.IsOverdue ? "Document rejected and overdue for resubmission." : "Document was rejected and requires resubmission.");

            return CreateActionCandidate(act, NextActionKind.DocumentResubmission, ResponsibleParty.Client, act.Title, reason,
                sla, sla.IsOverdue ? 1 : 2, isRejected: true, currentStageNumber);
        }

        var awaitingVerification = act.Status == ClientActionStatus.Uploaded || docState == LinkedDocumentState.CompliantUnverified;
        if (awaitingVerification)
        {
            // Rank 9: Document compliant but unverified / action in Uploaded (Rank 8 when overdue)
            var reason = sla.IsOverdue
                ? "Document submitted and overdue for staff verification."
                : "Document submitted and awaiting staff verification.";

            return CreateActionCandidate(act, NextActionKind.DocumentVerification, ResponsibleParty.Staff, $"Verify {act.Title}", reason,
                sla, sla.IsOverdue ? 8 : 9, isRejected: false, currentStageNumber);
        }

        if (act.Status != ClientActionStatus.Pending)
        {
            return null;
        }

        var isClientParty = string.Equals(act.AssignedToRole, ResponsibleParty.Client, StringComparison.OrdinalIgnoreCase);
        if (isClientParty)
        {
            var isEvidence = act.LinkedDocumentId.HasValue || EvidenceActionTypes.Contains(act.Type);
            if (isEvidence)
            {
                // Rank 4: Pending document upload action (Rank 1 when overdue)
                var reason = !string.IsNullOrWhiteSpace(act.Description)
                    ? act.Description
                    : (sla.IsOverdue ? "Action is overdue." : "Document upload required.");

                return CreateActionCandidate(act, NextActionKind.DocumentUpload, ResponsibleParty.Client, act.Title, reason,
                    sla, sla.IsOverdue ? 1 : 4, isRejected: false, currentStageNumber);
            }

            // Rank 7: Other pending client tasks (Rank 1 when overdue)
            var taskReason = !string.IsNullOrWhiteSpace(act.Description)
                ? act.Description
                : (sla.IsOverdue ? "Action is overdue." : "Client action required.");

            return CreateActionCandidate(act, NextActionKind.ClientTask, ResponsibleParty.Client, act.Title, taskReason,
                sla, sla.IsOverdue ? 1 : 7, isRejected: false, currentStageNumber);
        }

        // Rank 11: Other pending staff tasks (Rank 8 when overdue)
        var staffReason = sla.IsOverdue
            ? "Staff task is overdue for review."
            : (!string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Staff task required.");

        return CreateActionCandidate(act, NextActionKind.StaffTask, ResponsibleParty.Staff, act.Title, staffReason,
            sla, sla.IsOverdue ? 8 : 11, isRejected: false, currentStageNumber);
    }

    private static CandidateItem CreateActionCandidate(
        ClientAction act,
        string kind,
        string party,
        string title,
        string reason,
        SlaStatus sla,
        int rank,
        bool isRejected,
        int currentStageNumber) => new()
    {
        Item = new NextActionItem
        {
            Kind = kind,
            ResponsibleParty = party,
            Title = title,
            Reason = reason,
            ActionId = act.ActionId,
            SourceType = act.SourceType,
            SourceId = act.LinkedDocumentId ?? act.LinkedConditionId ?? act.LinkedRequirementId ?? act.ActionId,
            StageNumber = act.StageNumber,
            DueAtUtc = sla.DueAtUtc,
            IsOverdue = sla.IsOverdue,
            OverdueBy = sla.OverdueBy,
            PriorityRank = rank
        },
        IsRejected = isRejected,
        IsCurrentStage = act.StageNumber == currentStageNumber,
        CreatedAt = act.CreatedAt
    };

    private static LinkedDocumentState GetLinkedDocumentState(ClientAction act, IReadOnlyList<DocumentSummaryDto>? documents)
    {
        if (!act.LinkedDocumentId.HasValue || documents == null)
        {
            return LinkedDocumentState.None;
        }

        // Deleted documents are ignored.
        var doc = documents.FirstOrDefault(d => d.DocumentId == act.LinkedDocumentId.Value && !d.IsDeleted);
        if (doc == null)
        {
            return LinkedDocumentState.None;
        }

        if (string.Equals(doc.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
        {
            return LinkedDocumentState.Verified;
        }

        if (string.Equals(doc.ComplianceStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            return LinkedDocumentState.ComplianceRejected;
        }

        if (string.Equals(doc.ComplianceStatus, "Compliant", StringComparison.OrdinalIgnoreCase))
        {
            return LinkedDocumentState.CompliantUnverified;
        }

        return LinkedDocumentState.None;
    }

    private static string? ExtractRejectionReason(string? sourceMetadata)
    {
        if (string.IsNullOrWhiteSpace(sourceMetadata))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(sourceMetadata);
            foreach (var name in new[] { "verificationReason", "rejectionReason" })
            {
                if (doc.RootElement.TryGetProperty(name, out var prop) &&
                    prop.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(prop.GetString()))
                {
                    return prop.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Ignore non-JSON metadata
        }

        return null;
    }

    private static void AttachActionId(CandidateItem candidate, Guid actionId)
    {
        if (candidate.Item.ActionId.HasValue)
        {
            return;
        }

        var item = candidate.Item;
        candidate.Item = new NextActionItem
        {
            Kind = item.Kind,
            ResponsibleParty = item.ResponsibleParty,
            Title = item.Title,
            Reason = item.Reason,
            ActionId = actionId,
            SourceType = item.SourceType,
            SourceId = item.SourceId,
            StageNumber = item.StageNumber,
            DueAtUtc = item.DueAtUtc,
            IsOverdue = item.IsOverdue,
            OverdueBy = item.OverdueBy,
            PriorityRank = item.PriorityRank
        };
    }

    private static NextActionItem SanitizeClientItem(NextActionItem item) => new()
    {
        Kind = item.Kind,
        ResponsibleParty = item.ResponsibleParty,
        Title = item.Title,
        Reason = item.Reason,
        ActionId = item.ActionId,
        SourceType = item.SourceType,
        SourceId = null,      // staff view only
        StageNumber = item.StageNumber,
        DueAtUtc = item.DueAtUtc,
        IsOverdue = item.IsOverdue,
        OverdueBy = null,     // staff view only
        PriorityRank = 0      // staff view only
    };

    private static NextActionItem CreateClientSafeStaffReviewBlocker() => new()
    {
        Kind = NextActionKind.StaffTask,
        ResponsibleParty = ResponsibleParty.Staff,
        Title = "Review in progress",
        Reason = StaffReviewBlockerReason
    };

    private static void AddUnavailableBlockers(List<NextActionItem> blockers, NextActionInputs inputs)
    {
        if (inputs.IsDocumentsUnavailable)
        {
            blockers.Add(CreateUnavailableBlocker("Document Service Unavailable", DocumentUnavailableReason));
        }

        if (inputs.IsConditionsUnavailable)
        {
            blockers.Add(CreateUnavailableBlocker("Condition Status Unavailable", ConditionUnavailableReason));
        }
    }

    private static NextActionItem CreateUnavailableBlocker(string title, string reason) => new()
    {
        Kind = NextActionKind.Unavailable,
        ResponsibleParty = ResponsibleParty.Staff,
        Title = title,
        Reason = reason,
        PriorityRank = 999
    };

    private static SlaStatus GetActionSla(ClientAction action, IReadOnlyDictionary<Guid, SlaStatus>? statuses, DateTimeOffset now)
    {
        if (statuses != null && statuses.TryGetValue(action.ActionId, out var existing))
        {
            return existing;
        }

        if (action.DeadlineUtc.HasValue)
        {
            var isOverdue = action.DeadlineUtc.Value < now.UtcDateTime;
            var overdueBy = isOverdue ? now.UtcDateTime - action.DeadlineUtc.Value : (TimeSpan?)null;
            return new SlaStatus(action.DeadlineUtc.Value, isOverdue, overdueBy);
        }

        return new SlaStatus(null, false, null);
    }

    /// <summary>
    /// Requirements carry no deadline of their own; their SLA comes from the mirrored ClientAction
    /// (LinkedRequirementId) unless an explicit requirement SLA with a due date was supplied.
    /// </summary>
    private static SlaStatus GetRequirementSla(Requirement req, NextActionInputs inputs, DateTimeOffset now)
    {
        SlaStatus? existing = null;
        if (inputs.SlaStatuses != null && inputs.SlaStatuses.TryGetValue(req.RequirementId, out var found))
        {
            existing = found;
            if (found.DueAtUtc.HasValue)
            {
                return found;
            }
        }

        var mirrored = inputs.Actions?
            .Where(a => a.LinkedRequirementId == req.RequirementId &&
                        a.Status is not (ClientActionStatus.Completed or ClientActionStatus.Cancelled))
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.ActionId)
            .FirstOrDefault();

        if (mirrored != null)
        {
            return GetActionSla(mirrored, inputs.SlaStatuses, now);
        }

        return existing ?? new SlaStatus(null, false, null);
    }

    private static SlaStatus GetConditionSla(EngagementCondition cond, IReadOnlyDictionary<Guid, SlaStatus>? statuses, DateTimeOffset now)
    {
        if (statuses != null && statuses.TryGetValue(cond.ConditionId, out var existing))
        {
            return existing;
        }

        if (cond.DueDateUtc.HasValue)
        {
            var isOverdue = cond.DueDateUtc.Value < now.UtcDateTime;
            var overdueBy = isOverdue ? now.UtcDateTime - cond.DueDateUtc.Value : (TimeSpan?)null;
            return new SlaStatus(cond.DueDateUtc.Value, isOverdue, overdueBy);
        }

        return new SlaStatus(null, false, null);
    }

    private sealed class CandidateItem
    {
        public NextActionItem Item { get; set; } = null!;
        public bool IsRejected { get; init; }
        public bool IsCurrentStage { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
