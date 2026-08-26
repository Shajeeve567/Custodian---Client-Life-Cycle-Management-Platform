# AGENT HANDOVER KNOWLEDGE & CONTEXT GUIDE

> **Note for Future AI Agents**: Read this document first before starting any work on this project in a new chat session. It contains critical architectural decisions, team member scope rules, git policies, fixed gotchas, and next steps.

---

## 1. Project Overview & Current State

- **Platform**: Custodian — Client Life-Cycle Management Platform
- **Backend Tech Stack**: .NET 9 Web API Microservices, Entity Framework Core, MySQL (Pomelo provider), xUnit & Moq.
- **Solution Location**: `backend/Custodian.sln`
- **Current Feature Branch**: `CSTD-39-append-only-event-logging`
- **Current Status**:
  - **Story 6 (Append-Only Event Logging)** core implementation is complete in `backend/src/services/Audit`.
  - Database context (`AuditDbContext`), models (`AuditEvent`), repository (`AuditEventRepository`), service (`AuditEventService`), and controller (`AuditEventsController`) are fully functional.
  - Test suite (`backend/tests/Custodian.Audit.Tests`) contains 11 passing unit and integration tests.
  - Solution builds cleanly (`dotnet build backend/Custodian.sln`) with 0 errors.

---

## 2. STRICT RULES & TEAMMATE SCOPE BOUNDARIES

### 🚨 Rule 1: Git & Branch Safety (CRITICAL)
- **NEVER EVER touch, merge into, or edit the `main` or `dev` branches directly.**
- **NEVER execute auto-merges, pull requests, or merge commands targeting `main` or `dev`.**
- Always work on dedicated feature branches (e.g. `CSTD-39-append-only-event-logging` or `CSTD-15-engagement-crud`).
- **DO NOT run `git push` without explicit user request/approval.**
- **Docs Strategy**: Do not touch or delete `.md` files in `Docs/` unless instructed. Do not force-commit non-code documentation files to remote PRs if team policy excludes doc commits.

### 🚨 Rule 2: Teammate Scope (Shajeeve's Scope vs. Ilzam's Scope)
- **Shajeeve's Scope (DO NOT TOUCH)**:
  - `backend/src/services/Identity`
  - `backend/src/services/ClientRegistry`
  - Sprint 1 Identity & Client onboarding code.
  - **DO NOT edit or modify Shajeeve's microservices or files unless explicitly instructed by the user.**
- **Ilzam's Scope (Our Scope)**:
  - `backend/src/services/Workflow` (Story 2: Engagement CRUD & Lifecycle Management)
  - `backend/src/services/Audit` (Story 6: Append-Only Event Logging Service)
  - `backend/tests/Custodian.Workflow.Tests`
  - `backend/tests/Custodian.Audit.Tests`

---

## 3. Git Workflow & Conflict Resolution Strategy

### A. How to Handle PR Merge Conflicts (e.g., between Ilzam and Shajeeve on `dev`)
1. **Never merge dev directly in remote GitHub UI without local resolution**:
   - If PR shows "Cannot automatically merge", DO NOT touch remote branches or attempt force-merges.
2. **Recommended Local Resolution Workflow**:
   ```bash
   git checkout dev
   git pull origin dev
   git checkout <your-feature-branch>
   git rebase dev
   # Fix any local conflicts in appsettings, gitignore, or migration files
   git add <resolved-files>
   git rebase --continue
   dotnet build backend/Custodian.sln  # Verify clean build after rebase
   ```

### B. Gitignore & Build Output Traps
- Ensure `bin/`, `obj/`, `*.user`, `msbuild.log`, and environment secret overrides (`appsettings.Development.json` if local DB credentials vary) are tracked in `.gitignore`.
- If gitignore conflicts occur between teammates, verify root `.gitignore` ignores build outputs before staging.

---

## 4. Key Architecture & Design Decisions

### A. Story 6 — Append-Only Event Logging Service (`backend/src/services/Audit`)
1. **Append-Only Immutability**:
   - `IAuditEventRepository` intentionally supports **ONLY** `AddAsync` and query methods (`GetByEngagementIdAsync`, `GetByTenantIdAsync`, `GetByIdAsync`).
   - **NO `Update` or `Delete` methods exist** in the repository or service layers to guarantee an immutable event log.
2. **Event Schema (`AuditEvent.cs`)**:
   - Fields: `EventId`, `EngagementId`, `TenantId`, `Actor`, `Type`, `Timestamp`, `Payload`, `SequenceNumber`, `Hash`, `PreviousHash`.
   - `Payload` must strictly be valid JSON format (enforced by `AuditEventService`).
   - `Hash` is a SHA-256 hash computed over sequence, tenant, engagement, actor, event type, timestamp, payload, and previous hash for cryptographic tamper detection.
3. **Multi-Tenant Isolation**:
   - `AuditEventsController` extracts `tenant_id` claim from JWT `ClaimsPrincipal` or HTTP header `X-Tenant-ID`.
   - All queries and writes are strictly scoped to the tenant ID.

### B. Story 2 — Engagement Lifecycle & Workflow Service (`backend/src/services/Workflow`)
- Manages engagement status transitions (`Draft` -> `Active` -> `Review` -> `Completed` / `Archived`).
- Uses `WorkflowDbContextFactory` for EF Core design-time CLI migrations.

---

## 5. Gotchas, Common Pitfalls & Fixed Errors

1. **Compilation Error CS0246 in Test Projects (`FactAttribute` / `Xunit`)**:
   - **Symptom**: Roslyn compilation fails with missing `FactAttribute` when building test projects.
   - **Cause**: .NET 9 SDK implicit global usings do not automatically pull in `Xunit` attributes into all namespaces.
   - **Solution**: Explicitly add `using Xunit;` at the top of test `.cs` files in `Custodian.Audit.Tests` and `Custodian.Workflow.Tests`.

2. **Incorrect Solution Build Command**:
   - **Symptom**: Running `dotnet build Custodian.sln` from workspace root fails with `MSB1009: Project file does not exist`.
   - **Solution**: Always specify the exact solution path: `dotnet build backend/Custodian.sln`.

3. **EF Core `IDesignTimeDbContextFactory` Purpose**:
   - **Why it exists**: `dotnet ef migrations add` needs to construct the `DbContext` at CLI design-time without launching the full web application.
   - **Gotcha**: If `WorkflowDbContextFactory` or `AuditDbContextFactory` is missing or fails connection string lookup, `dotnet ef` CLI fails with `Unable to create an object of type 'DbContext'`.
   - **Pomelo MySQL Provider**: Always specify MySQL server version in `DbContext` options (e.g. `ServerVersion.AutoDetect` or explicit MySQL version).

4. **Tenant Security Validation**:
   - Never trust client-submitted `TenantId` parameters in HTTP request bodies without validating them against the authenticated caller's JWT claims (`tenant_id`) or API gateway headers (`X-Tenant-ID`).

---

## 6. Recommended Next Steps for the Next Agent

1. **Genesis Event Integration**:
   - Wire up `Workflow` service engagement creation to emit/trigger a `Genesis` audit event in the `Audit` service.
2. **Kafka / Async Event Consumption**:
   - Implement background event consumption infrastructure for asynchronous audit event processing.
3. **Documentation Sync**:
   - Keep `Docs/Story 2 and subtasks.md` and project backlog updated as progress continues.
