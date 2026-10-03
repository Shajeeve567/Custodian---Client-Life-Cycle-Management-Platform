# CSTD-183: Validation & Verification Report Live QA Verification Evidence

- **Date / Time of Verification:** 2026-10-03T19:00:00Z (2026-10-04 00:30 UTC+5:30)
- **Target Tenant:** `2f0557d4-45cf-4b62-84d5-c03486405f9a` (QA-CSTD21-UI)
- **Target Environment:** Azure MySQL QA Databases (`qa_document_db`, `qa_workflow_db`, `qa_identity_db`)
- **Git Branch:** `feature/CSTD-31-validation-verification-report`
- **Result:** **PASS** (Staff live access: Manual Staff login still required due to absence of Staff account in baseline tenant)

---

## 1. Controlled Fixture Summary

Seven active document fixtures were seeded into `qa_document_db.documents` under Tenant `2f0557d4-45cf-4b62-84d5-c03486405f9a`.

### Fixture Document Records

| # | Document ID | Engagement | Type | UploadedAt (UTC) | Compliance Status | Human Verification | Auto Rejection Reason | Human Verification Reason |
| :-: | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | `a0000001-0000-0000-0000-000000000001` | `dd3ef3bd-109d-43c1-8a10-6b0a347e7bf1` | `Passport` | `2026-10-10 10:00:00` | `Compliant` | `Verified` | *(null)* | *(null)* |
| 2 | `a0000001-0000-0000-0000-000000000002` | `dd3ef3bd-109d-43c1-8a10-6b0a347e7bf1` | `UtilityBill` | `2026-10-15 09:00:00` | `Compliant` | `Rejected` | *(null)* | `CSTD31-FIXTURE-HUMAN-NAME-MISMATCH` |
| 3 | `a0000001-0000-0000-0000-000000000003` | `dd3ef3bd-109d-43c1-8a10-6b0a347e7bf1` | `BankStatement` | `2026-10-18 14:00:00` | `Compliant` | `Pending` | *(null)* | *(null)* |
| 4 | `a0000001-0000-0000-0000-000000000004` | `dd3ef3bd-109d-43c1-8a10-6b0a347e7bf1` | `Passport` | `2026-10-20 23:30:00` | `Rejected` | `Unverified` | `CSTD31-FIXTURE-AUTO-EXPIRED` | *(null)* |
| 5 | `a0000001-0000-0000-0000-000000000005` | `649d247d-de40-49e5-a8a9-babd6ec028aa` | `UtilityBill` | `2026-10-18 16:00:00` | `Compliant` | `Unverified` | *(null)* | *(null)* |
| 6 | `a0000001-0000-0000-0000-000000000006` | `649d247d-de40-49e5-a8a9-babd6ec028aa` | `BankStatement` | `2026-10-19 11:00:00` | `Rejected` | `Unverified` | `CSTD31-FIXTURE-AUTO-BLURRY` | *(null)* |
| 7 | `a0000001-0000-0000-0000-000000000007` | `649d247d-de40-49e5-a8a9-babd6ec028aa` | `Passport` | `2026-10-25 15:00:00` | `Pending` | `Unverified` | *(null)* | *(null)* |

All records have `is_deleted = false`.

---

## 2. Expected vs. Actual Totals (Unfiltered Live Population)

| Category | Metric | Expected | Actual | Status |
| :--- | :--- | :-: | :-: | :-: |
| **Summary** | Total uploads | 7 | 7 | **PASS** |
| **Automatic compliance** | Compliant | 4 | 4 | **PASS** |
| **Automatic compliance** | Rejected | 2 | 2 | **PASS** |
| **Automatic compliance** | Pending | 1 | 1 | **PASS** |
| **Human verification** | Verified | 1 | 1 | **PASS** |
| **Human verification** | Rejected | 1 | 1 | **PASS** |
| **Human verification** | Unverified | 1 | 1 | **PASS** |
| **Human verification** | Pending | 1 | 1 | **PASS** |
| **Document type** | Passport | 3 | 3 | **PASS** |
| **Document type** | BankStatement | 2 | 2 | **PASS** |
| **Document type** | UtilityBill | 2 | 2 | **PASS** |
| **Automatic rejection reason** | `CSTD31-FIXTURE-AUTO-BLURRY` | 1 | 1 | **PASS** |
| **Automatic rejection reason** | `CSTD31-FIXTURE-AUTO-EXPIRED` | 1 | 1 | **PASS** |
| **Human verification rejection reason** | `CSTD31-FIXTURE-HUMAN-NAME-MISMATCH` | 1 | 1 | **PASS** |

*Note: Human verification population strictly counts documents where `ComplianceStatus == Compliant` (Docs 1, 2, 3, 5). Docs 4, 6, 7 do not inflate human verification counts.*

---

## 3. Filter Verification Results

Live CSV export tests executed through `GET /api/reports/validation-verification?format=csv`:

### Test A: Unfiltered
- **Query:** `format=csv`
- **Expected:** Total 7 (Auto: 4 Compliant, 2 Rejected, 1 Pending; Human: 1 Verified, 1 Rejected, 1 Unverified, 1 Pending; Types: 3 Passport, 2 BankStatement, 2 UtilityBill; Auto Rejection: 1 Expired, 1 Blurry; Human Rejection: 1 Name Mismatch)
- **Actual:** Matched 100%
- **Result:** **PASS**

### Test B: Engagement 1 Only
- **Query:** `format=csv&engagementId=dd3ef3bd-109d-43c1-8a10-6b0a347e7bf1`
- **Expected:** Total 4 (Docs 1, 2, 3, 4; Auto: 3 Compliant, 1 Rejected, 0 Pending; Human: 1 Verified, 1 Rejected, 0 Unverified, 1 Pending; Types: 2 Passport, 1 BankStatement, 1 UtilityBill; Auto Rejection: 1 Expired; Human Rejection: 1 Name Mismatch)
- **Actual:** Matched 100%
- **Result:** **PASS**

### Test C: Engagement 2 Only
- **Query:** `format=csv&engagementId=649d247d-de40-49e5-a8a9-babd6ec028aa`
- **Expected:** Total 3 (Docs 5, 6, 7; Auto: 1 Compliant, 1 Rejected, 1 Pending; Human: 0 Verified, 0 Rejected, 1 Unverified, 0 Pending; Types: 1 BankStatement, 1 Passport, 1 UtilityBill; Auto Rejection: 1 Blurry)
- **Actual:** Matched 100%
- **Result:** **PASS**

### Test D: Date Range
- **Query:** `format=csv&from=2026-10-15&to=2026-10-20`
- **Expected:** Total 5 (Docs 2, 3, 4, 5, 6; Auto: 3 Compliant, 2 Rejected, 0 Pending; Human: 0 Verified, 1 Rejected, 1 Unverified, 1 Pending; Types: 2 BankStatement, 2 UtilityBill, 1 Passport; Auto Rejection: 1 Blurry, 1 Expired; Human Rejection: 1 Name Mismatch)
- **Actual:** Matched 100%
- **Result:** **PASS**

### Test E: Boundary Date Only
- **Query:** `format=csv&from=2026-10-15&to=2026-10-15`
- **Expected:** Total 1 (Doc 2; Auto: 1 Compliant, 0 Rejected, 0 Pending; Human: 0 Verified, 1 Rejected, 0 Unverified, 0 Pending; Types: 1 UtilityBill; Human Rejection: 1 Name Mismatch)
- **Actual:** Matched 100%
- **Result:** **PASS**

### Test F: Date + Engagement 2 Combined
- **Query:** `format=csv&from=2026-10-15&to=2026-10-20&engagementId=649d247d-de40-49e5-a8a9-babd6ec028aa`
- **Expected:** Total 2 (Docs 5, 6; Auto: 1 Compliant, 1 Rejected, 0 Pending; Human: 0 Verified, 0 Rejected, 1 Unverified, 0 Pending; Types: 1 BankStatement, 1 UtilityBill; Auto Rejection: 1 Blurry)
- **Actual:** Matched 100%
- **Result:** **PASS**

### Test G: Empty Date Range
- **Query:** `format=csv&from=2026-10-01&to=2026-10-05`
- **Expected:** Total 0 (All metrics 0, empty categories omitted from detail)
- **Actual:** Total 0
- **Result:** **PASS**

---

## 4. PDF / CSV Parity

- **Endpoint:** `GET /api/reports/validation-verification?format=pdf`
- **HTTP Status:** `200 OK`
- **Content-Type:** `application/pdf`
- **Content-Disposition:** `attachment; filename="custodian-validation_verification-20261003-1852Z.pdf"`
- **Binary Header:** `%PDF-1.4` (valid QuestPDF document stream)
- **Parity Confirmation:**
  Both PDF visual sections (`KeyValueSection` and `TableSection`) and the CSV detail table are constructed synchronously by `ValidationVerificationReportBuilder.Build()` from the identical `ValidationVerificationData` aggregate instance.
  All metrics match: Total 7, Automatic 4/2/1, Human 1/1/1/1, Types 3/2/2, Reasons 1/1/1.
- **Result:** **PASS**

---

## 5. Staff Live Access Check

- **Assigned Staff ID in Fixtures:** `e4474a2a-c86a-448c-8038-6388ed5a3a5b`
- **Identity Account Inspection:**
  In `qa_identity_db`, user `e4474a2a-c86a-448c-8038-6388ed5a3a5b` is `qa.cstd21.owner.500e0e@test.com` with role `Owner`.
  The only members of tenant `2f0557d4-45cf-4b62-84d5-c03486405f9a` are:
  - `qa.cstd21.owner.500e0e@test.com` (`Owner`)
  - `qa.cstd21.client.500e0e@test.com` (`Client`)
  - `qa.pr47.client01@test.com` (`Client`)
- **Status:** **MANUAL STAFF LOGIN REQUIRED**
  Per instructions: No user roles were altered, no accounts modified, and no credentials reset. Automated test coverage in `ReportsControllerTests.cs` and `WorkflowReportEngagementScopeResolverTests.cs` fully validates Staff scope restrictions (staff-scoped filtering, zero-leak when outside allowed engagements, 503 fail-closed when Workflow is unavailable).

---

## 6. Tenant Isolation Evidence

- **Database Verification:**
  `SELECT DISTINCT tenant_id FROM documents WHERE document_id LIKE 'a0000001-%'` returned strictly `2f0557d4-45cf-4b62-84d5-c03486405f9a`.
- **Query Isolation:**
  `ValidationVerificationReportService.cs` filters:
  ```csharp
  var query = _dbContext.Documents
      .AsNoTracking()
      .Where(d => d.TenantId == normalizedTenantId && !d.IsDeleted);
  ```
- **Claim Enforcement:**
  `ReportsController.cs` extracts `tenantId` exclusively from authenticated JWT claims (`User.FindFirst("tenant_id")?.Value`), preventing cross-tenant request spoofing.
- **Result:** **PASS**

---

## 7. Empty and Boundary Date Summary

- Single boundary date (`from=2026-10-15&to=2026-10-15`): **Total = 1** (**PASS**)
- Empty date range (`from=2026-10-01&to=2026-10-05`): **Total = 0** (**PASS**)
- In-range span (`from=2026-10-15&to=2026-10-20`): **Total = 5** (**PASS**)

---

## 8. Fixture IDs and Cleanup Script

### Fixture Document IDs
```text
a0000001-0000-0000-0000-000000000001
a0000001-0000-0000-0000-000000000002
a0000001-0000-0000-0000-000000000003
a0000001-0000-0000-0000-000000000004
a0000001-0000-0000-0000-000000000005
a0000001-0000-0000-0000-000000000006
a0000001-0000-0000-0000-000000000007
```

### Cleanup SQL Statement
```sql
DELETE FROM documents 
WHERE tenant_id = '2f0557d4-45cf-4b62-84d5-c03486405f9a'
  AND document_id IN (
    'a0000001-0000-0000-0000-000000000001',
    'a0000001-0000-0000-0000-000000000002',
    'a0000001-0000-0000-0000-000000000003',
    'a0000001-0000-0000-0000-000000000004',
    'a0000001-0000-0000-0000-000000000005',
    'a0000001-0000-0000-0000-000000000006',
    'a0000001-0000-0000-0000-000000000007'
  );
```
