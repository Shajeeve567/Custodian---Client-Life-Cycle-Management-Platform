# CSTD-42: Audit Flag & Archive — Developer Verification Evidence

## Overview
This document records developer verification evidence for **CSTD-42 (Audit Flag & Archive)** and its developer subtasks (`CSTD-241`, `CSTD-242`, `CSTD-243`, `CSTD-244`). 

> **Important**: `CSTD-245` is assigned to independent QA (Shajeeve Balakrishnan) and is NOT claimed complete by development.

---

## Live Jira Requirements Mapping

| Subtask | Requirement Summary | Implementation Files | Automated Test Coverage | Status |
| :--- | :--- | :--- | :--- | :---: |
| **CSTD-241** | **Implement flag/archive metadata foundation**: Store flag/archive metadata outside immutable cryptographic payload/hash fields in `audit_event_metadata`; expose authorized tenant-scoped commands; provide minimal Flag/Archive UI actions. | - `backend/src/services/Audit/Models/AuditEventMetadata.cs`<br>- `backend/src/services/Audit/Data/AuditDbContext.cs`<br>- Migration `20261004171259_AddAuditEventMetadata.cs`<br>- `backend/src/services/Audit/Controllers/AuditEventsController.cs`<br>- `frontend/src/components/AuditLogView.tsx`<br>- `frontend/src/services/api.ts` | - `AuditEventMetadataTests.cs` (Unit)<br>- `AuditFlagArchiveEndpointTests.cs` (Integration: Owner/Staff flag & archive actions, DTO mapping, persistence) | **COMPLETE** |
| **CSTD-242** | **Implement reference-event creation**: Create new reference event `AuditEventFlagged` to record flag action instead of mutating original; duplicate delivery safe; actor/reason preserved. | - `backend/src/services/Audit/Repositories/AuditEventRepository.cs`<br>- `backend/src/services/Audit/Services/AuditEventService.cs`<br>- `frontend/src/components/audit/describeAuditEvent.ts` | - `AuditFlagArchiveTests.cs` (Unit)<br>- `AuditFlagArchiveEndpointTests.cs` (Integration: reference event verification, duplicate call idempotency, parallel race prevention) | **COMPLETE** |
| **CSTD-243** | **Ensure verification behavior unaffected**: Confirm archived events remain part of chain verification; original payload/hash unchanged; verification result independent of archive status. | - `backend/src/services/Audit/Services/AuditEventService.cs`<br>- `backend/src/services/Audit/Repositories/AuditEventRepository.cs` | - `AuditFlagArchiveTests.cs` (Unit)<br>- `AuditFlagArchiveEndpointTests.cs` (Integration: `Owner_ArchiveEvent_Success_ModifiesMetadataOnly_PreservesVerification`) | **COMPLETE** |
| **CSTD-244** | **Dev Unit/Integration/Data Integrity Tests**: Unit + real ASP.NET Core HTTP integration tests; cryptographic immutability; role authorization; anti-disclosure; deterministic CI execution. | - `backend/tests/Custodian.Audit.Tests/Unit/AuditFlagArchiveTests.cs`<br>- `backend/tests/Custodian.Audit.Tests/Integration/AuditFlagArchiveEndpointTests.cs` | - 130 passing tests (100% pass rate)<br>- 16/16 real HTTP pipeline integration tests passing locally and in CI | **COMPLETE** |
| **CSTD-245** | **QA: Functional/security/acceptance validation** | Assigned to Shajeeve Balakrishnan | Independent QA validation | **INDEPENDENT QA (NOT CLAIMED)** |

---

## Key Architectural Proofs

### 1. Cryptographic Immutability of Original Events
- The original event row in `events` (`event_id`, `engagement_id`, `tenant_id`, `actor`, `type`, `timestamp`, `payload`, `sequence_number`, `hash`, `previous_hash`) is NEVER updated or modified on Flag or Archive.
- All flag/archive state is maintained in the dedicated table `audit_event_metadata`.
- For Flag actions, an immutable `AuditEventFlagged` event is appended to the SHA-256 hash chain with payload:
  ```json
  {
    "referencedEventId": "<original-id>",
    "referencedEventType": "<original-type>",
    "referencedSequenceNumber": 1,
    "reason": "Tamper suspected"
  }
  ```
- For Archive actions, metadata is stored strictly in `audit_event_metadata` (`is_archived = true`, `archive_reason`, `archived_by`, `archived_at`). The hash chain is NOT advanced, and zero events are appended.

### 2. Archived Events Verification Invariance (CSTD-243)
- `VerifyChainAsync` queries `events` ordered by `sequence_number`.
- Archived events remain in `events` and are verified against their SHA-256 hash and previous hash.
- Archiving an event does not mutate its hash or invalidate the cryptographic chain.
- Deterministic automated proof: `AuditFlagArchiveEndpointTests.Owner_ArchiveEvent_Success_ModifiesMetadataOnly_PreservesVerification`.

### 3. Idempotency & Concurrency Race Elimination
- In `AuditEventRepository.FlagEventOnceAsync`:
  1. The transaction is initiated (`READ COMMITTED`).
  2. The engagement's chain head row (`engagement_chain_heads`) is locked first via `FOR UPDATE`.
  3. The authoritative idempotency check on `_context.EventMetadata.FirstOrDefaultAsync(m => m.EventId == eventId)` occurs **inside the serialized transaction while holding the lock**.
  4. Concurrent calls serialize on the lock; the second call observes `existingMetadata.IsFlagged == true` and immediately returns `Created: false` without creating duplicate reference events or advancing the chain.
- Verified by unit tests (`Flag_Concurrency_MultipleParallelRequests_AppendsExactlyOneReferenceEvent`) and HTTP integration tests (`Flag_RepeatedCall_IsIdempotent_DoesNotDuplicateReferenceEvent`).

### 4. Authorization & Anti-Disclosure
- Role restrictions: `[Authorize(Roles = "Owner,Staff")]`.
- Clients calling `/flag` or `/archive` receive `403 Forbidden`.
- Unauthenticated requests receive `401 Unauthorized`.
- Staff callers undergo engagement assignment validation via `IEngagementAccessClient`:
  - Assigned Staff: `200 OK`.
  - Unassigned Staff: `404 Not Found` (anti-disclosure pattern: does not disclose whether the event or engagement exists).
- Tenant mismatch between token and query parameter returns `403 Forbidden`. Cross-tenant event modification attempts return `404 Not Found`.

---

## Build and Test Verification

### Backend Service Build
```
dotnet build backend/src/services/Audit/Audit.csproj --configuration Release
Result: Succeeded with 0 Warnings, 0 Errors.
```

### Audit Test Suite
```
dotnet test backend/tests/Custodian.Audit.Tests/Custodian.Audit.Tests.csproj --configuration Release
Result: Passed: 130, Failed: 0, Skipped: 5 (MySQL probe tests skip safely when no live MySQL instance is present).
```

### Frontend Production Build
```
cd frontend && npm run build
Result: TypeScript typecheck and Vite production build succeeded in 11.08s.
```

---

## 7. Pomelo MySQL Runtime Blocker & Targeted Repair

### Preflight Discovery & Failure
During manual UI test preflight against live Azure MySQL (`qa_audit_db`), calling `GET /api/audit-events` or `GET /api/audit-events/engagement/{id}` failed with **HTTP 500**:
```text
System.InvalidOperationException: The LINQ expression '@__idList_1' could not be translated.
Primitive collections support has not been enabled.
   at Custodian.Audit.Repositories.AuditEventRepository.GetMetadataForEventsAsync(IEnumerable`1 eventIds, Guid tenantId) line 179
```

### Root Cause
In EF Core 9 with `Pomelo.EntityFrameworkCore.MySql` (9.0.0-preview.2), evaluating `idList.Contains(m.EventId)` where `idList` is an in-memory `List<Guid>` triggers EF Core's primitive collection translation, which Pomelo's visitor rejects as unsupported.

### Surgical Repair
In `backend/src/services/Audit/Repositories/AuditEventRepository.cs`:
Wrapped `idList` with `EF.Constant(idList)` in `GetMetadataForEventsAsync`:
```csharp
var list = await _context.EventMetadata
    .AsNoTracking()
    .Where(m => m.TenantId == tenantId && EF.Constant(idList).Contains(m.EventId))
    .ToListAsync();
```
This causes EF Core to translate the in-memory GUID collection directly to standard SQL `WHERE a.tenant_id = @p AND a.event_id IN ('guid1', 'guid2')`, fully executed server-side in MySQL without client evaluation.

### Live Azure MySQL Verification
Verified against `qa_audit_db` using QA workspace `2f0557d4-45cf-4b62-84d5-c03486405f9a` and engagement `dd3ef3bd-109d-43c1-8a10-6b0a347e7bf1`:
- `GET /api/audit-events?tenantId=...`: **200 OK** (135 events returned with hydrated metadata)
- `GET /api/audit-events/engagement/dd3ef3bd...`: **200 OK** (41 events returned with hydrated metadata)
- Metadata fields (`isFlagged: false`, `isArchived: false`) hydrated properly without HTTP 500.
- Cross-tenant isolation verified: 0 non-tenant events returned.

### Regression Coverage & Honest Limitations
- **Unit/Pipeline Regression**: Added `Metadata_GetMetadataForEvents_TranslatesSuccessfullyUnderPomeloMySql` in `AuditEventMetadataTests.cs`. It invokes `IQueryable.ToQueryString()` configured with Pomelo's MySQL provider to deterministically assert that Pomelo translates the `EF.Constant` query to SQL `IN (...)` in CI without requiring a live MySQL daemon.
- **Provider Limitation**: InMemory database tests do not exercise SQL expression translation visitors, which is why this provider-specific issue bypassed earlier InMemory unit tests. Offline `ToQueryString()` plus live Azure MySQL verification guarantee the fix works under real Pomelo MySQL.
