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
| `GET` | `/api/audit-events/verify?engagementId=` | Owner/Staff | Verify that engagement's hash chain (**new in Sprint 3**) |

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
