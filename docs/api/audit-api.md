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
| `GET` | `/api/audit-events/verify?engagementId=` | Owner/Staff | Verify that engagement's hash chain (**Sprint 3**) |
| `GET` | `/api/reports/proof-of-delivery?engagementId=&format=pdf` | Owner/Staff | Generate and download Proof-of-Delivery PDF report (**Sprint 4, CSTD-43**) |

User tokens (any role) cannot append events; only services holding the ingestion key can. The key value is configured as `AuditIngestion__ApiKey` and is never committed.

## Shapes

```jsonc
// CreateAuditEventRequest
{ "eventId": "guid?", "engagementId": "guid", "tenantId": "guid?", "actor": "string", "type": "string", "payload": "{}" }

// AuditEventResponse
{
  "eventId", "engagementId", "tenantId", "actor", "type", "timestamp",
  "payload": "json string",
  "sequenceNumber": 0,
  "hash": "64 hex chars",
  "previousHash": "64 hex chars"
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

## Proof-of-Delivery Report (CSTD-43)

`GET /api/reports/proof-of-delivery?engagementId={guid}&format=pdf`

Generates an authenticated, tamper-evident Proof-of-Delivery PDF report containing the engagement summary, chronological audit event timeline, live SHA-256 hash-chain verification status, and verified history boundary.

### Query Parameters
- `engagementId` (GUID, required): The target engagement. Missing/empty/malformed GUID returns `400 Bad Request` with field `"engagementId"`.
- `format` (string, optional, default: `"pdf"`): Desired format. Currently only `pdf` is supported. Unsupported formats return `400 Bad Request` with field `"format"`.

### Authorization & Tenant Isolation
- **Roles:** `[Authorize(Roles = "Owner,Staff")]`. Client callers receive `403 Forbidden`. Requests without valid bearer tokens receive `401 Unauthorized`.
- **Tenant Scope:** Tenant is resolved strictly from the JWT `tenant_id` claim. Cross-tenant engagement requests return `404 Not Found` without disclosing existence.
- **Staff Access:** Staff users may only access engagements they are assigned to. Audit verifies assignment with Workflow (`GET /api/engagements/{id}/can-access`); unassigned Staff receive `404 Not Found`.

### Response Headers
- `Content-Type: application/pdf`
- `Content-Disposition: attachment; filename="custodian-proof_of_delivery-{timestamp}Z.pdf"`
- `Cache-Control: no-store`
- `X-Content-Type-Options: nosniff`

### Verification Outcomes & Boundaries
- **Integrity Status:**
  - `VALID`: Cryptographically unbroken chain from genesis to tip.
  - `TAMPERED`: Hash mismatch or broken chain link detected; broken event ID and sequence number reported with failure notice.
  - `EMPTY`: Ledger has no recorded events; renders empty notice.
  - `UNAVAILABLE`: Verification could not complete due to dependency fault; reports `UNAVAILABLE` and is **never** labelled valid.
- **Concurrent Append / Live Head Consistency:** If the live database tip advances during generation, the report documents a historical snapshot boundary and warns that subsequent appended events are unverified, avoiding false claims of live-head verification.
- **Delivery Confirmation Disclaimer:** Confirms mathematical integrity of the audit history; does not infer delivery completion merely from an engagement being Closed or having audit records, unless an explicit delivery domain event is present.
