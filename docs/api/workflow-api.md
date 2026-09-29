# Workflow API (Sprint 3 additions)

This page documents the Workflow service endpoints added or changed in Sprint 3 (16 Sep – 30 Sep 2026). Earlier endpoints (engagement create/list/status/stage, requirements, client portal) are unchanged and are listed at the end for reference only.

Local base URL: `http://localhost:5225`. Interactive docs: `/scalar/v1` in Development.

## Common rules

- **Authentication.** Every endpoint requires a JWT bearer token issued by the Identity service (`Authorization: Bearer <token>`).
- **Roles.** `Owner`, `Staff`, `Client`. "Owner/Staff" means `[Authorize(Roles = "Owner,Staff")]`; Clients get `403`.
- **Staff scoping.** A `Staff` user only sees engagements where they are the responsible staff member (`Engagement.StaffId`). Owners see the whole tenant.
- **Tenant.** The tenant comes from the JWT `tenant_id` claim. The optional `?tenantId=` query parameter must match it; a mismatch returns `403`. With no tenant at all the API returns `400`.
- **Errors.** Validation errors return `400` (ModelState or `{ "message": "..." }`), missing resources `404`, rule conflicts `409`. Report endpoints return RFC 7807 ProblemDetails (see [reporting.md](../reporting.md)).

---

## Next action — CSTD-19 / CSTD-35

| Method | Route | Role |
|---|---|---|
| `GET` | `/api/engagements/{id}/next-action` | Owner/Staff |
| `POST` | `/api/engagements/{id}/recheck` | Owner/Staff |

Both return a `NextActionResult`. It is computed from live state on every call (no cache). Mutation endpoints do not return it; the frontend re-fetches after a change. `recheck` is idempotent and changes nothing — it is the "confirm the state after an intervention" call. Clients get their (reduced) next action through the client portal endpoints.

```jsonc
// NextActionResult
{
  "engagementId": "guid",
  "engagementStatus": "Draft | Started | Closed | Cancelled",
  "currentStage": "Onboarding | DocumentCollection | Verification | Execution | Closure",
  "overallState": "Closed | NotStarted | ClientActionRequired | AwaitingStaff | ReadyToAdvance | BlockedExternal | AllComplete",
  "primaryAction": NextActionItem | null,
  "blockers": [NextActionItem],
  "nextStageGate": { "targetStage": "string", "isSatisfied": true, "reasons": ["string"] } | null, // staff view only
  "upcomingConditions": [NextActionItem],   // informational, staff view only
  "isStalled": false,
  "evaluatedAtUtc": "datetimeoffset"
}

// NextActionItem
{
  "kind": "RequirementSubmission | RequirementReview | DocumentUpload | DocumentResubmission | DocumentVerification | ConditionApproval | ConditionPayment | ClientTask | StaffTask | AdvanceStage | Unavailable",
  "responsibleParty": "Client | Staff",
  "title": "string",
  "reason": "string",
  "actionId": "guid?",
  "sourceType": "string?",
  "sourceId": "guid?",        // staff view only
  "stageNumber": 1,
  "dueAtUtc": "datetime?",
  "isOverdue": false,
  "overdueBy": "timespan?",   // staff view only
  "priorityRank": 0           // 0–14, staff view only
}
```

## Responsible staff — CSTD-34

| Method | Route | Role | Body |
|---|---|---|---|
| `PUT` | `/api/Engagements/{id}/staff` | **Owner** | `{ "staffId": "string (max 36)" }` |

Changes `Engagement.StaffId`. Returns the updated `EngagementResponse`. Audited as `ResponsibleStaffChanged`.

---

## Client actions — CSTD-21

Base route: `/api/engagements/{engagementId}/actions`

| Method | Route | Role | Notes |
|---|---|---|---|
| `GET` | `/` | any (Clients: own engagement only) | List actions |
| `POST` | `/` | Owner/Staff | `CreateClientActionDto` |
| `GET` | `/standard-checklist/preview` | Owner/Staff | Lists the tasks the standard checklist would add (current and later stages, skipping existing ones). Adds nothing |
| `POST` | `/standard-checklist` | Owner/Staff | Adds those tasks; returns the added actions. `409` on a rule conflict |
| `PATCH` | `/{actionId}` | Owner/Staff | Edit a Pending staff-managed task. Requirement- or condition-linked tasks return `409` |
| `PUT` | `/{actionId}/cancel` | Owner/Staff | `{ "reason": "string" }`. Kept as Cancelled, never deleted. Completed or linked tasks return `409` |
| `PUT` | `/{actionId}/complete` | any (scoped) | Mark complete |
| `PUT` | `/{actionId}/upload` | any (scoped) | Record an evidence upload |
| `PUT` | `/{actionId}/review` | Owner/Staff | Staff review |
| `PUT` | `/{actionId}/verification` | Owner/Staff | `{ "verificationStatus": "Verified | Rejected", "verifiedBy": "string", "verificationReason": "string?" }` |

```jsonc
// CreateClientActionDto
{
  "title": "string (required, max 200)",
  "description": "string?",
  "type": "DocumentUpload",              // required, max 50
  "stageNumber": 1,                      // 1–5
  "deadlineUtc": "datetime?",
  "source": "string (required, max 100)",
  "isInternalOnly": false,
  "assignedToRole": "Client",
  "sourceMetadata": "string?",
  "sourceType": "string?",
  "linkedDocumentId": "guid?",
  "linkedConditionId": "guid?",
  "linkedMeetingId": "guid?"
  // createdBy is set by the controller from the JWT
}

// UpdateClientActionDto (PATCH) — all optional
{ "title": "string?", "description": "string?", "deadlineUtc": "datetime?", "clearDeadline": false, "stageNumber": 1 }
```

---

## Engagement conditions — CSTD-24

Base route: `/api/engagements/{engagementId}/conditions`

| Method | Route | Role | Body / query |
|---|---|---|---|
| `POST` | `/` | Owner/Staff | `AttachConditionDto` |
| `GET` | `/` | any | `?includeInactive=` (staff, default `true`). Clients get `ClientSafeConditionDto[]` for their own engagement only |
| `GET` | `/{conditionId}` | any | Staff: `ConditionResponseDto`; Client: `ClientSafeConditionDto` |
| `PATCH` | `/{conditionId}` | Owner/Staff | `UpdateConditionDto` |
| `PUT` | `/{conditionId}/deactivate` | Owner/Staff | `{ "reason": "string (required, max 500)" }` |

```jsonc
// AttachConditionDto
{
  "type": "Approval | Payment",          // required
  "requiredBeforeStage": "Execution",    // default; must be later than the current stage
  "title": "string (required, max 200)",
  "description": "string?",
  "dueDateUtc": "datetime?",
  "amount": 0.01,                        // Payment only, > 0
  "currency": "USD",                     // ^[A-Z]{3}$
  "paymentType": "Upfront | Milestone | Final",
  "internalNote": "string?"              // staff only, never shown to clients or written to audit
}

// UpdateConditionDto — all optional
{ "title", "description", "dueDateUtc", "amount", "currency", "paymentType", "internalNote" }

// ConditionResponseDto (staff)
{
  "conditionId", "engagementId", "tenantId", "type", "isActive",
  "status": "Pending | Satisfied | Rejected",
  "requiredBeforeStage", "title", "description", "dueDateUtc",
  "amount", "currency", "paymentType", "internalNote",
  "createdBy", "createdAt", "updatedBy", "updatedAt",
  "deactivatedBy", "deactivatedAt", "deactivationReason",
  "satisfiedAt", "satisfiedBy", "isOverdue"
}
```

`ClientSafeConditionDto` has the same business fields but no `internalNote` and no audit fields.

> **Known gap.** The service has a guarded status transition (Pending → Satisfied | Rejected, Rejected → Pending), but no endpoint calls it in the current code, so a condition cannot yet be marked Satisfied through the API.

---

## SLA and stall — CSTD-33 / CSTD-34

| Method | Route | Role | Returns |
|---|---|---|---|
| `GET` | `/api/engagements/{engagementId}/stall` | Owner/Staff | `StallStatusDto` |
| `GET` | `/api/stall-queue` | Owner/Staff | `StallQueueItemDto[]` + `X-Total-Count` header |

`/stall` is computed on read:

```jsonc
{ "engagementId", "isStalled": true, "actionId", "actionTitle", "stageNumber", "deadlineUtc", "hoursOverdue", "evaluatedAtUtc" }
```

`/stall-queue` query parameters:

| Parameter | Default | Meaning |
|---|---|---|
| `mine` | `false` | Only engagements where the caller is the responsible staff member. Forced to `true` for Staff |
| `stage` | — | Only engagements in this stage (e.g. `DocumentCollection`) |
| `minOverdueHours` | — | Only engagements at least this many hours overdue |
| `page` | `1` | 1-based |
| `pageSize` | `25` | Max `100` |

The body is always a plain array (empty when nothing is stalled, never `404`), sorted by `urgencyScore` descending. `X-Total-Count` is the total before paging.

```jsonc
// StallQueueItemDto
{
  "engagementId", "tenantId", "clientId", "staffId", "engagementStage",
  "blockerActionId", "blockerActionTitle", "blockerStageNumber",
  "nextAction", "nextActionResponsibleParty",   // advisory
  "deadlineUtc", "hoursOverdue", "stalledSinceUtc", "openStallCount",
  "urgencyScore", "evaluatedAtUtc"
}
```

---

## Interventions — CSTD-35

Base route: `/api/engagements/{engagementId}/interventions`

| Method | Route | Role | Returns |
|---|---|---|---|
| `POST` | `/` | Owner/Staff | `201` + `InterventionResponse` |
| `GET` | `/` | Owner/Staff | `InterventionResponse[]` |

```jsonc
// RecordInterventionRequest
{
  "type": "Meeting | RecoveryAction",   // default RecoveryAction
  "reason": "string (required, max 2000)",
  "outcome": "Recovered | Progressing | NoChange | Escalated",  // required
  "blockerActionId": "guid?",
  "stallId": "guid?"
}

// InterventionResponse
{ "interventionId", "engagementId", "tenantId", "stallId", "blockerActionId", "type", "reason", "outcome", "recordedBy", "createdAt" }
```

Recording an intervention never changes the blocker action or the stage. Call `POST /recheck` afterwards to see the current state.

---

## Reports — CSTD-36 / CSTD-37

| Method | Route | Role |
|---|---|---|
| `GET` | `/api/reports/sla-performance` | Owner/Staff |

Query: `from`, `to`, `engagementId`, `stage`, `staffId`, `actionType`, `format=pdf|csv`. Returns a file download. See [reporting.md](../reporting.md) for the full contract, file naming and error format.

---

## Unchanged endpoints (for reference)

- `api/Engagements`: `POST`, `GET`, `GET {id}`, `PUT {id}/status`, `PUT {id}/stage`, `DELETE {id}`
- `api/engagements/{engagementId}/requirements`: `GET`, `POST`, `PUT {requirementId}/submit`, `PUT {requirementId}/review`
- `api/portal`: `GET my-engagement`, `GET engagements/{engagementId}`
