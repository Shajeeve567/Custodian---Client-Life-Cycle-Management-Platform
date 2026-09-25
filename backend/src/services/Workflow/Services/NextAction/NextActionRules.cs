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

        var candidates = new List<CandidateItem>();

        // -------------------------------------------------------------
        // 1. Process Requirements (Ranks 1, 3, 8, 10)
        // -------------------------------------------------------------
        var handledRequirementIds = new HashSet<Guid>();
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

                handledRequirementIds.Add(req.RequirementId);
                var sla = GetRequirementSla(req, inputs.SlaStatuses, now);

                if (string.Equals(req.Status, RequirementStatus.Submitted, StringComparison.OrdinalIgnoreCase))
                {
                    // Staff review
                    var rank = sla.IsOverdue ? 8 : 10;
                    candidates.Add(new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.RequirementReview,
                            ResponsibleParty = ResponsibleParty.Staff,
                            Title = $"Review requirement: {req.Type}",
                            Reason = sla.IsOverdue
                                ? $"Submitted requirement '{req.Type}' is awaiting review (Overdue)."
                                : $"Submitted requirement '{req.Type}' is awaiting staff review.",
                            SourceType = "Requirement",
                            SourceId = req.RequirementId,
                            StageNumber = req.StageNumber ?? currentStageNumber,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = rank
                        },
                        IsRejected = false,
                        IsCurrentStage = (req.StageNumber ?? currentStageNumber) == currentStageNumber,
                        CreatedAt = req.CreatedAt
                    });
                }
                else
                {
                    // Requested or Rejected requirement (Client submission)
                    var isRejected = string.Equals(req.Status, RequirementStatus.Rejected, StringComparison.OrdinalIgnoreCase);
                    var rank = sla.IsOverdue ? 1 : 3;
                    var reason = isRejected
                        ? (!string.IsNullOrWhiteSpace(req.RejectionReason) ? $"Requirement rejected: {req.RejectionReason}" : $"Requirement '{req.Type}' was rejected and requires resubmission.")
                        : $"Information '{req.Type}' is required.";

                    candidates.Add(new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.RequirementSubmission,
                            ResponsibleParty = ResponsibleParty.Client,
                            Title = $"Submit requirement: {req.Type}",
                            Reason = reason,
                            SourceType = "Requirement",
                            SourceId = req.RequirementId,
                            StageNumber = req.StageNumber ?? currentStageNumber,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = rank
                        },
                        IsRejected = isRejected,
                        IsCurrentStage = (req.StageNumber ?? currentStageNumber) == currentStageNumber,
                        CreatedAt = req.CreatedAt
                    });
                }
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

                if (string.Equals(cond.Type, ConditionType.Approval.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var isRejected = string.Equals(cond.Status, ConditionStatus.Rejected, StringComparison.OrdinalIgnoreCase);
                    var rank = sla.IsOverdue ? 1 : 5;
                    var reason = isRejected
                        ? (!string.IsNullOrWhiteSpace(cond.InternalNote) ? $"Approval was declined: {cond.InternalNote}" : "Approval was declined.")
                        : $"Approval required before advancing to {nextStage.Value}.";

                    candidates.Add(new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.ConditionApproval,
                            ResponsibleParty = ResponsibleParty.Client,
                            Title = cond.Title,
                            Reason = reason,
                            SourceType = "Condition",
                            SourceId = cond.ConditionId,
                            StageNumber = (int)cond.RequiredBeforeStage,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = rank
                        },
                        IsRejected = isRejected,
                        IsCurrentStage = true,
                        CreatedAt = cond.CreatedAt
                    });
                }
                else if (string.Equals(cond.Type, ConditionType.Payment.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    var rank = sla.IsOverdue ? 1 : 6;
                    var title = view == NextActionView.Staff ? $"Confirm payment: {cond.Title}" : "Payment due";
                    var reason = view == NextActionView.Staff
                        ? "Confirm payment received."
                        : $"Payment of {cond.Amount:F2} {cond.Currency} is required before advancing to {nextStage.Value}.";

                    candidates.Add(new CandidateItem
                    {
                        Item = new NextActionItem
                        {
                            Kind = NextActionKind.ConditionPayment,
                            ResponsibleParty = ResponsibleParty.Client,
                            Title = title,
                            Reason = reason,
                            SourceType = "Condition",
                            SourceId = cond.ConditionId,
                            StageNumber = (int)cond.RequiredBeforeStage,
                            DueAtUtc = sla.DueAtUtc,
                            IsOverdue = sla.IsOverdue,
                            OverdueBy = sla.OverdueBy,
                            PriorityRank = rank
                        },
                        IsRejected = false,
                        IsCurrentStage = true,
                        CreatedAt = cond.CreatedAt
                    });
                }
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

                // Only current stage or earlier leftover actions
                if (act.StageNumber > currentStageNumber)
                {
                    continue;
                }

                // Deduplicate with mirrored requirements
                if (act.LinkedRequirementId.HasValue && handledRequirementIds.Contains(act.LinkedRequirementId.Value))
                {
                    var existingReqCandidate = candidates.FirstOrDefault(c =>
                        c.Item.SourceType == "Requirement" && c.Item.SourceId == act.LinkedRequirementId.Value);
                    if (existingReqCandidate != null && !existingReqCandidate.Item.ActionId.HasValue)
                    {
                        existingReqCandidate.Item = new NextActionItem
                        {
                            Kind = existingReqCandidate.Item.Kind,
                            ResponsibleParty = existingReqCandidate.Item.ResponsibleParty,
                            Title = existingReqCandidate.Item.Title,
                            Reason = existingReqCandidate.Item.Reason,
                            ActionId = act.ActionId,
                            SourceType = existingReqCandidate.Item.SourceType,
                            SourceId = existingReqCandidate.Item.SourceId,
                            StageNumber = existingReqCandidate.Item.StageNumber,
                            DueAtUtc = existingReqCandidate.Item.DueAtUtc,
                            IsOverdue = existingReqCandidate.Item.IsOverdue,
                            OverdueBy = existingReqCandidate.Item.OverdueBy,
                            PriorityRank = existingReqCandidate.Item.PriorityRank
                        };
                    }
                    continue;
                }

                // Linked document compliance & verification check
                var isDocRejected = false;
                var isDocUnverified = false;
                if (act.LinkedDocumentId.HasValue && inputs.Documents != null)
                {
                    var doc = inputs.Documents.FirstOrDefault(d => d.DocumentId == act.LinkedDocumentId.Value && !d.IsDeleted);
                    if (doc != null)
                    {
                        if (string.Equals(doc.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
                        {
                            // Verified document is not a blocker
                            continue;
                        }

                        if (string.Equals(doc.ComplianceStatus, "Rejected", StringComparison.OrdinalIgnoreCase))
                        {
                            isDocRejected = true;
                        }
                        else if (string.Equals(doc.ComplianceStatus, "Compliant", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(doc.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase))
                        {
                            isDocUnverified = true;
                        }
                    }
                }

                var sla = GetActionSla(act, inputs.SlaStatuses, now);
                var isClientParty = string.Equals(act.AssignedToRole, ResponsibleParty.Client, StringComparison.OrdinalIgnoreCase);

                if (isClientParty)
                {
                    if (sla.IsOverdue)
                    {
                        // Rank 1: Overdue client item
                        var isRejected = act.Status == ClientActionStatus.Rejected || isDocRejected;
                        var kind = isRejected
                            ? NextActionKind.DocumentResubmission
                            : (act.Type == "DocumentUpload" || act.LinkedDocumentId.HasValue ? NextActionKind.DocumentUpload : NextActionKind.ClientTask);

                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = kind,
                                ResponsibleParty = ResponsibleParty.Client,
                                Title = act.Title,
                                Reason = isRejected
                                    ? (!string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Document rejected and overdue for resubmission.")
                                    : (!string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Action is overdue."),
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.LinkedDocumentId ?? act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = true,
                                OverdueBy = sla.OverdueBy,
                                PriorityRank = 1
                            },
                            IsRejected = isRejected,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
                    else if (act.Status == ClientActionStatus.Rejected || isDocRejected)
                    {
                        // Rank 2: Rejected document/evidence
                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = NextActionKind.DocumentResubmission,
                                ResponsibleParty = ResponsibleParty.Client,
                                Title = act.Title,
                                Reason = !string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Document was rejected and requires resubmission.",
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.LinkedDocumentId ?? act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = false,
                                OverdueBy = null,
                                PriorityRank = 2
                            },
                            IsRejected = true,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
                    else if (act.Status == ClientActionStatus.Pending && (act.Type == "DocumentUpload" || act.LinkedDocumentId.HasValue))
                    {
                        // Rank 4: Pending document upload action
                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = NextActionKind.DocumentUpload,
                                ResponsibleParty = ResponsibleParty.Client,
                                Title = act.Title,
                                Reason = !string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Document upload required.",
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.LinkedDocumentId ?? act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = false,
                                OverdueBy = null,
                                PriorityRank = 4
                            },
                            IsRejected = false,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
                    else if (act.Status == ClientActionStatus.Pending)
                    {
                        // Rank 7: Other pending client tasks in current stage
                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = NextActionKind.ClientTask,
                                ResponsibleParty = ResponsibleParty.Client,
                                Title = act.Title,
                                Reason = !string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Client action required.",
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = false,
                                OverdueBy = null,
                                PriorityRank = 7
                            },
                            IsRejected = false,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
                }
                else
                {
                    // Staff party item
                    if (sla.IsOverdue)
                    {
                        // Rank 8: Overdue staff item
                        var kind = act.Status == ClientActionStatus.Uploaded || isDocUnverified
                            ? NextActionKind.DocumentVerification
                            : NextActionKind.StaffTask;

                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = kind,
                                ResponsibleParty = ResponsibleParty.Staff,
                                Title = act.Title,
                                Reason = "Staff task is overdue for review.",
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.LinkedDocumentId ?? act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = true,
                                OverdueBy = sla.OverdueBy,
                                PriorityRank = 8
                            },
                            IsRejected = false,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
                    else if (act.Status == ClientActionStatus.Uploaded || isDocUnverified)
                    {
                        // Rank 9: Document compliant but unverified / action in Uploaded
                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = NextActionKind.DocumentVerification,
                                ResponsibleParty = ResponsibleParty.Staff,
                                Title = $"Verify {act.Title}",
                                Reason = "Document submitted and awaiting staff verification.",
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.LinkedDocumentId ?? act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = false,
                                OverdueBy = null,
                                PriorityRank = 9
                            },
                            IsRejected = false,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
                    else if (act.Status == ClientActionStatus.Pending)
                    {
                        // Rank 11: Other pending staff tasks
                        candidates.Add(new CandidateItem
                        {
                            Item = new NextActionItem
                            {
                                Kind = NextActionKind.StaffTask,
                                ResponsibleParty = ResponsibleParty.Staff,
                                Title = act.Title,
                                Reason = !string.IsNullOrWhiteSpace(act.Description) ? act.Description : "Staff task required.",
                                ActionId = act.ActionId,
                                SourceType = act.SourceType,
                                SourceId = act.ActionId,
                                StageNumber = act.StageNumber,
                                DueAtUtc = sla.DueAtUtc,
                                IsOverdue = false,
                                OverdueBy = null,
                                PriorityRank = 11
                            },
                            IsRejected = false,
                            IsCurrentStage = act.StageNumber == currentStageNumber,
                            CreatedAt = act.CreatedAt
                        });
                    }
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
            else if (inputs.IsDocumentsUnavailable)
            {
                // Dependency failure: Documents unavailable, cannot claim ReadyToAdvance
                overallState = OverallState.BlockedExternal;
                primaryAction = null;
                blockers.Add(CreateUnavailableBlocker());
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

                if (inputs.IsDocumentsUnavailable)
                {
                    blockers.Add(CreateUnavailableBlocker());
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

                    if (inputs.IsDocumentsUnavailable)
                    {
                        blockers.Add(CreateUnavailableBlocker());
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

                    if (inputs.IsDocumentsUnavailable)
                    {
                        blockers.Add(CreateUnavailableBlocker());
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

    private static NextActionItem CreateUnavailableBlocker() => new()
    {
        Kind = NextActionKind.Unavailable,
        ResponsibleParty = ResponsibleParty.Staff,
        Title = "Document Service Unavailable",
        Reason = DocumentUnavailableReason,
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

    private static SlaStatus GetRequirementSla(Requirement req, IReadOnlyDictionary<Guid, SlaStatus>? statuses, DateTimeOffset now)
    {
        if (statuses != null && statuses.TryGetValue(req.RequirementId, out var existing))
        {
            return existing;
        }

        return new SlaStatus(null, false, null);
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
