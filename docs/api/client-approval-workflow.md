# Client Approval Workflow API & Event Specification (CSTD-25)

This document specifies the REST API contracts, security rules, state transitions, and asynchronous events for the Client Approval Workflow introduced under **CSTD-25** (subtasks **CSTD-142** through **CSTD-146**).

---

## 1. Overview & Business Model

The Client Approval Workflow allows firm staff to attach formal approval conditions to an engagement. When an approval condition is activated:
1. It targets the engagement's primary client (`TargetClientId` derived from `Engagement.ClientId`).
2. It sets a strict deadline (`DueDateUtc`) and requirement stage gate (`RequiredBeforeStage`).
3. An active `ClientAction` (kind: `ConditionApproval`) is generated for the client portal.
4. An `ApprovalAttached` event is published to Kafka, triggering the Identity notification engine (in-app portal + email).
5. The engagement pipeline gate blocks stage progression until the condition is satisfied (`Approved`) or disabled.
6. The client can either **Approve** or **Reject** the approval request via authenticated portal endpoints.
7. Decisions are recorded with full audit trails (`ApprovalCompleted`, `ApprovalRejected`).
8. Next Action and blocker calculations immediately re-evaluate based on the decision.

---

## 2. State & Data Model (CSTD-142)

### Approval Statuses
An approval condition maps to one of three lifecycle statuses:
- **`Pending`**: Attached, active, awaiting client decision (`SatisfiedAt == null`, `RejectionReason == null`).
- **`Approved`**: Client explicitly approved (`SatisfiedAt != null`, `SatisfiedByClientId != null`). Satisfies the condition and unlocks the stage gate.
- **`Rejected`**: Client explicitly rejected (`SatisfiedAt == null`, `RejectionReason != null`, `SatisfiedByClientId != null`). The condition remains unsatisfied and continues blocking stage advance until staff resolves the concern.

### Target Client & Deadline Semantics
- **`TargetClientId`**: In Custodian's domain model, each engagement belongs to exactly one authoritative client (`Engagement.ClientId`). The target client for all engagement-level approvals is deterministically derived from `Engagement.ClientId`. This enforces 1-to-1 client scoping, eliminates data redundancy, and prevents cross-client assignment bugs.
- **`DueDateUtc`**: Optional or required deadline for the approval. When set, overdue checks flag the approval as stalled / overdue in both staff Next Action queues and client portal action lists.

---

## 3. Endpoints & Authorization (CSTD-143)

### 3.1 Attach / Activate Approval Condition
Staff attaches and activates an Approval condition on an engagement.

- **Route:** `POST /api/engagements/{engagementId}/conditions`
- **Authorized Roles:** `Owner`, `Staff` (Staff must be assigned to engagement; Owner has tenant-wide access)
- **Request Body:**
  ```json
  {
    "type": "Approval",
    "title": "Tax Strategy Sign-off",
    "description": "Please review and approve the attached fiscal year tax plan.",
    "requiredBeforeStage": "Execution",
    "dueDateUtc": "2026-10-15T18:00:00Z",
    "internalNote": "Staff preparation complete. Risk score: Low."
  }
  ```
- **Responses:**
  - `201 Created`: Condition attached and activated. Returns `ConditionResponseDto`.
  - `400 Bad Request`: Validation failure (e.g., missing title or invalid stage).
  - `403 Forbidden`: User is client or staff not assigned to this engagement.
  - `404 Not Found`: Engagement does not exist or tenant mismatch.
  - `409 Conflict`: Engagement is closed or cancelled.

### 3.2 Client Request & Status Retrieval
Client inspects the details and status of an approval condition.

- **Route:** `GET /api/engagements/{engagementId}/conditions/{conditionId}`
- **Authorized Roles:** `Owner`, `Staff`, `Client`
- **Security Scoping:**
  - If caller is `Client`, their `client_id` claim **must match** `Engagement.ClientId`.
  - Staff notes (`InternalNote`) and internal metadata are stripped. Returns `ClientSafeConditionDto`.
- **Response (`ClientSafeConditionDto`):**
  ```json
  {
    "conditionId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "engagementId": "2bf85f64-5717-4562-b3fc-2c963f66afa1",
    "type": "Approval",
    "title": "Tax Strategy Sign-off",
    "description": "Please review and approve the attached fiscal year tax plan.",
    "requiredBeforeStage": "Execution",
    "dueDateUtc": "2026-10-15T18:00:00Z",
    "status": "Pending",
    "isSatisfied": false,
    "satisfiedAt": null,
    "rejectionReason": null,
    "targetClientId": "client-12345"
  }
  ```

### 3.3 Client Approve Endpoint
Client submits approval.

- **Route:** `POST /api/engagements/{engagementId}/conditions/{conditionId}/approve`
- **Authorized Roles:** `Client`
- **Intended Client Restriction:** The caller's `client_id` claim MUST match `Engagement.ClientId`. Any mismatch returns `403 Forbidden`.
- **Preconditions & Guards:**
  - Condition must exist and belong to the specified engagement.
  - Condition must be active (`IsActive == true`). Inactive conditions return `409 Conflict`.
  - Engagement must be in active state (`Started`). Closed or cancelled engagements return `409 Conflict`.
- **Idempotency & Stale Decisions:**
  - If the condition is already `Approved` by this client, the endpoint returns `200 OK` with the existing condition state without creating duplicate events.
  - If the condition is already `Rejected`, attempting to approve returns `409 Conflict` (requires staff reactivation or intervention).
- **Responses:**
  - `200 OK`: Returns updated `ClientSafeConditionDto` with `status: "Approved"`, `isSatisfied: true`.
  - `403 Forbidden`: Mismatched client ID or non-client role.
  - `404 Not Found`: Condition not found.
  - `409 Conflict`: Engagement closed, condition disabled, or stale/conflicting state.

### 3.4 Client Reject Endpoint
Client submits rejection with a required reason.

- **Route:** `POST /api/engagements/{engagementId}/conditions/{conditionId}/reject`
- **Authorized Roles:** `Client`
- **Intended Client Restriction:** Caller's `client_id` claim MUST match `Engagement.ClientId`.
- **Request Body:**
  ```json
  {
    "reason": "The corporate address on page 3 is outdated. Please update before sign-off."
  }
  ```
- **Validation Rules:**
  - `reason` is required (non-null, non-empty, non-whitespace).
  - Maximum length: 500 characters.
  - Empty or invalid reasons return `400 Bad Request`.
- **Preconditions & Guards:**
  - Condition must be active (`IsActive == true`). Inactive returns `409 Conflict`.
  - Engagement must be active (`Started`). Closed or cancelled returns `409 Conflict`.
- **Idempotency & Stale Decisions:**
  - If already rejected with the same state, returns `200 OK` with existing rejection details.
  - If already approved, returns `409 Conflict`.
- **Outcome:**
  - Condition status becomes `Rejected`.
  - `SatisfiedAt` remains `null` (condition remains unsatisfied).
  - Stage progression remains blocked.
  - Linked `ClientAction` status is updated to `Rejected`.

---

## 4. Kafka Event Integration & Notification Pipeline (CSTD-144)

The Workflow service publishes domain events to Kafka using the shared `IKafkaProducer`:

| Event Name | Topic | Trigger | Payload Summary |
|---|---|---|---|
| `ApprovalAttached` | `custodian.workflow.events` | Staff attaches approval condition | `EngagementId`, `ConditionId`, `TenantId`, `ClientId`, `Title`, `DueDateUtc`, `RequiredBeforeStage` |
| `ApprovalCompleted` | `custodian.workflow.events` | Client approves condition | `EngagementId`, `ConditionId`, `TenantId`, `ClientId`, `SatisfiedAt` |
| `ApprovalRejected` | `custodian.workflow.events` | Client rejects condition | `EngagementId`, `ConditionId`, `TenantId`, `ClientId`, `RejectionReason`, `RejectedAt` |

### Initial Identity Notification Flow
1. When `ApprovalAttached` is published, Identity's event listener receives the message.
2. `EventToClientSafeMessageMapper` matches `ApprovalAttached` and formats an action notification:
   - *"Action required: Client approval requested for {Title}. Due: {DueDateUtc}."*
3. Identity persists an in-app portal notification and dispatches an email notification to the client user.

### Audit Evidence
1. The Audit service consumes `ApprovalAttached`, `ApprovalCompleted`, and `ApprovalRejected`.
2. Each event is recorded in the cryptographically chained audit log (`AuditEvent`) with tenant isolation, actor ID, and metadata.

---

## 5. Next Action & Stage Gate Effects

1. **Stage Gates:**
   - Any active approval condition assigned to `RequiredBeforeStage <= TargetStage` acts as a hard gate.
   - While `Pending` or `Rejected`, `GateEvaluationResult.IsSatisfied` is `false`, and `AdvanceStage` returns `409 Conflict`.
   - When `Approved`, the condition is satisfied and clears the gate blocker.
2. **Disabled Conditions:**
   - If staff deactivates/disables a condition (`IsActive = false`), gate evaluators ignore it completely.
3. **Next Action Computation:**
   - When pending: Primary Next Action points to `ConditionApproval` for `Client`.
   - When rejected: Primary Next Action shifts to `AwaitingStaff` / `StaffTask` indicating client rejection requires staff resolution.
