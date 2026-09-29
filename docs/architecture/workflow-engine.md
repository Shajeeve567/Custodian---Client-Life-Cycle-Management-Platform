# Workflow Engine (Sprint 3)

This page describes the workflow features delivered in Sprint 3 (16 Sep – 30 Sep 2026) and how they fit together. Endpoints are listed in [workflow-api.md](../api/workflow-api.md) and [audit-api.md](../api/audit-api.md).

```text
 Requirements ─┐
 ClientActions ┼──> NextActionRules (pure) ──> NextActionResult ──> workspace / portal
 Conditions ───┤          ▲
 Documents ────┘          │ IsStalled
                          │
 SlaCalculator ──> StallDetectionService (computed on read)
                          │
                          ├──> StallRecorder ──> StallRecord (one row per stall episode)
                          │                        └─> action.overdue / StallResolved events
                          └──> StallQueueService ──> GET /api/stall-queue (urgency-sorted)

 Intervention (Meeting | RecoveryAction) ──> recorded + audited, never mutates actions/stage
 ReportsController ──> SlaPerformanceReport ──> Custodian.Shared.Reporting (PDF / CSV)
 All audited events ──> Audit service ──> SHA-256 chain per engagement
```

## Next-action engine — CSTD-19

- `Services/NextAction/NextActionRules.cs` is a **pure, deterministic function** (`Decide(inputs, view, now)`): no I/O, no state changes, so it is table-tested directly. `NextActionService` loads the inputs (engagement, requirements, client actions, conditions, linked document state, stall flag) and calls it.
- It returns one **primary action**, the other **blockers** in priority order, the **next-stage gate** result, and **upcoming conditions** (conditions gating a stage after the next one; informational only).
- Priority ranks run from 0 to 14. Examples:
  - 0 — engagement Closed/Cancelled (`OverallState = Closed`) or Draft (staff primary "Activate engagement").
  - Client-side work (rejected documents/evidence, requirements to submit, pending uploads, other client tasks) ranks 2–7; client work that is **overdue is promoted to rank 1**.
  - Staff-side work (requirement reviews, compliant-but-unverified documents, other staff tasks) ranks 9–11; **overdue staff work is promoted to rank 8**.
  - 12 — gate satisfied → `AdvanceStage`; 13 — gate not satisfied; 14 — Closure stage with nothing open → `AllComplete`.
- Items that come from a requirement or condition and also have a mirrored `ClientAction` are emitted once (no duplicates).
- If Documents or condition data is unavailable, the result is `BlockedExternal` with an `Unavailable` item instead of a wrong answer.
- **Two views.** The staff view includes source ids, rank, overdue duration, the gate summary and upcoming conditions. The client view hides them.
- **Freshness.** Computed on every read, no cache. Writes do not return the next action; the UI re-fetches.

## Client action model — CSTD-21

`ClientAction` is the unit of work tracked per engagement stage (`StageNumber` 1–5). Actions can be:

- created by staff, individually or from the **standard checklist** (preview first, then apply; existing tasks are skipped);
- edited (`PATCH`) while Pending, if staff-managed;
- **cancelled with a reason** — kept as Cancelled, never deleted;
- completed, uploaded against, reviewed and verified.

Actions linked to a requirement or condition are managed through their source; editing or cancelling them directly returns `409`. Document-type mapping between action types and Documents service types lives in `frontend/src/constants/documentTypes.ts`.

## Engagement conditions — CSTD-24

`EngagementCondition` is an **Approval** or **Payment** condition that gates entry to a later stage (`RequiredBeforeStage`, default `Execution`; it must be later than the current stage when attached).

- Status: `Pending → Satisfied | Rejected`, `Rejected → Pending` (guarded transitions in `ConditionService`).
- Payment conditions carry `Amount`, ISO `Currency` and `PaymentType` (`Upfront | Milestone | Final`).
- Conditions are **deactivated with a reason**, not deleted.
- `InternalNote` is staff-only: never returned to clients and never written to audit payloads.
- The stage gate (`GateEvaluator`) blocks advancing into a stage while an active condition for that stage is not Satisfied. The next-action engine shows it as `ConditionApproval` / `ConditionPayment`.

> The current code has no endpoint that sets a condition to Satisfied or Rejected (the service method exists but is not called), so this transition is not yet reachable through the API.

## SLA and stall detection — CSTD-33

- **Due date** (`SlaCalculator`): the action's explicit deadline if set, otherwise `ActivatedAt + stage SLA`. Stage SLA hours come from config: `Sla:StageOverdueHours:<stageNumber>`, falling back to `Sla:DefaultOverdueHours` (72).
- **Stall status is computed on read** (`StallDetectionService`): there is **no background job or scheduler**. An engagement is stalled when its current client blocker is past its due date.
- **Stall episodes are persisted** (`StallRecord`, via `StallRecorder`): when a read finds a newly overdue action, a record is opened and `action.overdue` is published **once per episode** (a unique open-action key prevents duplicates across restarts and instances). When the action is no longer overdue the record is resolved (`ActionCompleted`, `ActionCancelled`, `ActionSubmitted`, `DeadlineExtended`, `EngagementClosed`, `Reassigned`) and `StallResolved` is published.
- The next-action engine only reads the stall flag; it never publishes events, so portal and workspace reads have no side effects.

## Staff stall queue — CSTD-34

`GET /api/stall-queue` lists the tenant's stalled engagements, most urgent first.

- **Urgency score** = hours overdue × weight. Blockers that gate the next stage use `Sla:Urgency:GatingWeight` (2.0); others use `Sla:Urgency:DefaultWeight` (1.0).
- Filters: `mine`, `stage`, `minOverdueHours`; paging with `X-Total-Count`.
- Staff only ever see their own engagements. Owners see the whole workspace and can reassign the responsible staff member (`PUT /api/Engagements/{id}/staff`).
- Frontend: `pages/StallQueuePage.tsx` (linked from `DashboardLayout`); the stall flag also shows in `components/NextActionPanel.tsx`.

## Intervention and recovery — CSTD-35

- Staff record an **intervention** against a stalled engagement: type `Meeting` or `RecoveryAction`, a reason, and an outcome (`Recovered`, `Progressing`, `NoChange`, `Escalated`), optionally linked to the blocker action and stall record.
- Recording an intervention is **audit only**: it never changes the blocker action and never advances the stage.
- Frontend: `components/RecordInterventionModal.tsx`.
- To see the effect, staff call `POST /api/engagements/{id}/recheck`, which re-runs the next-action engine (idempotent, returns the same shape as `GET /next-action`).

## Report infrastructure — CSTD-36 / CSTD-37

- Shared library `backend/src/shared/Custodian.Shared.Reporting`: PDF rendering (QuestPDF), RFC 4180 CSV, standard file naming, ProblemDetails errors and report telemetry.
- First report: **SLA Performance** (`GET /api/reports/sla-performance`, pdf or csv). It uses the same due-date rule as the stall queue (`SlaCalculator.ResolveDueAt`), so on-time vs late matches what staff saw in the queue.
- Full convention and report status: [reporting.md](../reporting.md). CSTD-37 is still in progress at sprint end.

## SHA-256 audit hash chain — CSTD-40

- Every audit event stores `Hash` and `PreviousHash`. A chain is scoped **per (tenant, engagement)**; the first event links to the genesis hash (64 zeros).
- The hash is SHA-256 over a **canonical JSON** form of `eventId`, `engagementId`, `tenantId`, `actor`, `type`, `timestamp` (UTC, round-trip format), `payload` (keys sorted recursively) and `previousHash`, with no whitespace. Changing the canonical form invalidates every existing hash, so it is treated as a wire contract.
- `EngagementChainHead` holds the tip of each chain. Appends lock that row (`SELECT … FOR UPDATE`), so the HTTP endpoint and the Kafka consumer (or two instances) cannot fork a chain. Heads are created lazily from the latest event, so no backfill was needed.
- `GET /api/audit-events/verify?engagementId=` recomputes the chain and reports the first event that does not match (payload edit, actor change, timestamp tamper or previous-hash substitution). Hash comparison is constant-time.
- Migrations: `AddPreviousHash`, `AddChainHeadsAndTextPayload`.

## Configuration summary (Workflow)

| Key | Default | Meaning |
|---|---|---|
| `Sla__DefaultOverdueHours` | `72` | SLA when a stage has no specific value |
| `Sla__StageOverdueHours__<n>` | — | SLA hours for stage `n` (1–5) |
| `Sla__Urgency__GatingWeight` | `2.0` | Urgency weight for stage-gating blockers |
| `Sla__Urgency__DefaultWeight` | `1.0` | Urgency weight for other blockers |
