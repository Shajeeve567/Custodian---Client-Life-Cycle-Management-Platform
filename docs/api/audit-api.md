# Audit API

The Audit service stores an append-only, tamper-evident event log. Since Sprint 3 (CSTD-40) every event is linked into a SHA-256 hash chain per engagement. See [workflow-engine.md](../architecture/workflow-engine.md#sha-256-audit-hash-chain--cstd-40) for how the chain works.

Local base URL: `http://localhost:5051`. The controller answers on two equivalent routes: `api/audit-events` and `api/events`.

Tenant rules match the Workflow API: the tenant comes from the JWT; an optional `?tenantId=` must match it (`403` otherwise). Staff are limited to engagements they are responsible for; Audit asks Workflow for this, so `Services__WorkflowUrl` must be set (Staff requests get `503` without it).

| Method | Route | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/audit-events` | Service key header `X-Audit-Ingestion-Key` (no user token) | Append an event over HTTP (used when `Audit__Transport=Http`) |
| `GET` | `/api/audit-events` | Owner/Staff | List events in the tenant |
| `GET` | `/api/audit-events/{id}` | Owner/Staff | One event |
| `GET` | `/api/audit-events/engagement/{engagementId}` | Owner/Staff | Events for one engagement |
| `GET` | `/api/audit-events/verify?engagementId=` | Owner/Staff | Verify that engagement's hash chain (**Sprint 3 / CSTD-40**) |
| `POST` | `/api/audit-events/{id}/flag` | Owner/Staff | Flag an event with reason, appending an immutable reference event (**Sprint 3 / CSTD-42**) |
| `POST` | `/api/audit-events/{id}/archive` | Owner/Staff | Archive an event with optional reason via metadata (**Sprint 3 / CSTD-42**) |

User tokens (any role) cannot append arbitrary domain events; only services holding the ingestion key can. Authenticated Staff and Owners can perform authorized management commands (`flag` and `archive`).

## Shapes

```jsonc
// CreateAuditEventRequest
{ "eventId": "guid?", "engagementId": "guid", "tenantId": "guid?", "actor": "string", "type": "string", "payload": "{}" }

// FlagAuditEventRequest (POST /{id}/flag)
{
  "reason": "Required explanation of why the event is flagged (1-500 characters)"
}

// ArchiveAuditEventRequest (POST /{id}/archive)
{
  "reason": "Optional reason for archiving (max 500 characters)"
}

// AuditEventResponse (includes CSTD-42 metadata)
{
  "eventId": "guid",
  "engagementId": "guid",
  "tenantId": "guid",
  "actor": "string",
  "type": "string",
  "timestamp": "iso-date",
  "payload": "json string",
  "sequenceNumber": 0,
  "hash": "64 hex chars",
  "previousHash": "64 hex chars",
  "isFlagged": false,
  "flagReason": "string?",
  "flaggedBy": "string?",
  "flaggedAt": "iso-date?",
  "flagReferenceEventId": "guid?",
  "isArchived": false,
  "archiveReason": "string?",
  "archivedBy": "string?",
  "archivedAt": "iso-date?"
}

// ChainVerificationResult (GET /verify)
{
  "engagementId": "guid",
  "isVerified": true,
  "count": 12,                 // events checked
  "brokenAtEventId": "guid?",  // first event whose hash does not match
  "reason": "string?"
}
```

`engagementId` is required on `/verify` (`400` when missing).

## Flag & Archive Integrity Rules (CSTD-42)

1. **Cryptographic Immutability**: The original `AuditEvent` row in `events` is never modified. Its `Payload`, `Hash`, `PreviousHash`, `SequenceNumber`, and `Timestamp` remain 100% untouched.
2. **Flag Creates an Immutable Reference Event**:
   - Marking an event as flagged records non-cryptographic metadata in `audit_event_metadata` and atomically appends an `AuditEventFlagged` event to the engagement's SHA-256 chain.
   - The reference event payload contains:
     ```json
     {
       "referencedEventId": "<original EventId>",
       "referencedEventType": "<original Type>",
       "referencedSequenceNumber": 12,
       "reason": "reason string"
     }
     ```
   - The reference event's ID is stored in `flagReferenceEventId`.
3. **Archive Metadata Only**:
   - Archiving updates `audit_event_metadata` only (`is_archived = true`, `archive_reason`, `archived_by`, `archived_at`).
   - No new audit event is appended to the chain.
   - Archived events remain fully part of chain verification and chain-order history.
4. **Idempotency**:
   - Calling `POST /{id}/flag` on an already flagged event returns the current state and does not append duplicate reference events or advance the chain head.
   - Calling `POST /{id}/archive` on an already archived event returns the current state idempotently without mutating the chain.
5. **Authorization & Tenant Isolation**:
   - Only `Owner` and `Staff` roles are authorized (`Client` receives `403 Forbidden`).
   - Staff access to an event's engagement is verified against Workflow assignment; unassigned Staff receive `404 Not Found` (anti-disclosure).
   - Cross-tenant requests are strictly rejected (`403` on claim mismatch, `404` or `409` on cross-tenant event IDs).
