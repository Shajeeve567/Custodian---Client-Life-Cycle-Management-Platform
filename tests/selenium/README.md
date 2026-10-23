# Custodian Selenium Automated Testing Suite (SE3112 Role D)

This directory contains the streamlined, high-signal automated Selenium browser test suite for the **Custodian — Client Lifecycle Management Platform**, implemented in **C#, .NET 10.0, xUnit, Selenium WebDriver (ChromeDriver), and PdfPig**.

---

## 1. Streamlined Test Suite Architecture (11 Tests)

The suite is structured into **6 high-signal microservice workflows** plus an isolated long-running regression suite. Every test runs in under 22 seconds, enforces cross-microservice boundaries, and is completely free of cosmetic DOM clicking.

### Core Workflow Sets (CI & Demo Sequence)
| Set ID | Method Name | Scope & Business Objective | Microservices Covered | Type |
|---|---|---|---|---|
| **AUTH01** | `AUTH01_RegistrationDuplicateRejectionAndProtectedRoutes` | Protected route interception, invalid credential rejection, and collision-resistant registration with `409 Conflict` duplicate protection. | Identity $\leftrightarrow$ Frontend | Mutating (Registration) |
| **AUTH02** | `AUTH02_RoleBasedAccessAndRouteGuardMatrix` | Single-pass role matrix: Owner workspace + *Manage Team*, Staff assigned card + hidden team controls, Client auto-landing in `/portal` with route guard redirect. | Identity $\leftrightarrow$ Frontend | Read-Only |
| **LIFE01** | `LIFE01_Owner_CreatesEngagementAndAssignsStaff_WithSelfCleanup` | Live dashboard search, client account provisioning, engagement initialization, staff assignment, multi-session staff view, and safe UI deletion. | Identity $\leftrightarrow$ Workflow | Mutating (Self-Cleaning via UI) |
| **LIFE02** | `LIFE02_Staff_StageGateRestrictionAndReadinessBlockers` | Stage 1 Draft readiness evaluation, Next Action engine blockers, and physical disablement of `Advance to Stage 2`. | Workflow | Read-Only |
| **APPR01** | `APPR01_StaffAttachesApprovalCondition_ClientRejectionAndGateBlocker` | **CSTD-25 Flagship Workflow**: Staff attaches approval condition with deadline, Client reviews pending approval card in portal, enforces mandatory rejection reason, Client rejects, and Staff confirms gate restriction. | Workflow $\leftrightarrow$ Identity Notifications $\leftrightarrow$ Portal | Dedicated Stage 2 Fixture |
| **DOC01** | `DOC01_Client_UploadValidationGuards_FileTypesAndSizeLimits` | Document compliance policy guards: non-PDF rejection (`.txt`), 5MB file size limit guard, mandatory compliance dates enforcement, and interactive submit transition. | Documents $\leftrightarrow$ Client Portal | Read-Only (Validation) |
| **DOC02** | `DOC02_MultiRole_DocumentRejection_Resubmission_AndVerification` | Full document review lifecycle: Client uploads synthetic evidence, Staff rejects with review reason, Client revision alert & replacement upload, Staff dual-sync verification. | Documents $\leftrightarrow$ Workflow $\leftrightarrow$ Portal | Dedicated Stage 2 Fixture |
| **STALL01** | `STALL01_Staff_StallQueueMonitoringAndFilterBehavior` | SLA stall detection monitoring (`/stall-queue`): overdue item urgency indicators (`<1h`, `24h`, `72h`), stage filtering, `Mine only` toggle, and operational intervention modal check. | Workflow (Next Action Engine) | Read-Only |
| **AUDIT01** | `AUDIT01_TamperEvidentHashChainVerification` | Immutable audit trail inspection, SHA-256 cryptographic event hash verification, and live UI chain verification control (`Intact`). | Audit $\leftrightarrow$ Workflow | Read-Only |
| **REPORT01** | `REPORT01_ProofOfDeliveryPdfDownloadAndAstValidation` | Headless Chrome automatic download configuration, binary signature verification (`%PDF-`), and deep structural/text AST extraction using `PdfPig`. | Audit $\leftrightarrow$ Reports | Read-Only (Closed Fixture) |
| **SEC01** | `SEC01_CrossClientAndCrossTenantAuthorization` | Cross-Client IDOR access denial (HTTP 403), JWT tenant-scoping rejection of forged `tenantId` query parameters, and role-restricted verification endpoints. | Security Authorization Gateway | Read-Only |

### Guarded Nightly Regression Suite (Separately Opt-In)
| Test ID | Method Name | Scope & Runtime | Guard |
|---|---|---|---|
| **E2E06** | `E2E06_FullClientLifecycle_CreationToClosure_GoldenPath` | Complete 5-Stage Golden Path (22 Checkpoints): Client provisioning, 7-task checklist, 3 compliance uploads, review, milestone sign-off, handoff, ledger seal at 100%, and audit chain (~9m 23s runtime). | **Opt-In Guard (`RUN_GOLDEN_PATH_E2E=true`)** |

---

## 2. Prerequisites & Environment Setup

1. **Running Microservices & Frontend**:
   - Identity Service: `http://localhost:5281` (Database: `qa_identity_db`)
   - Workflow Service: `http://localhost:5225` (Database: `qa_workflow_db`)
   - Audit Service: `http://localhost:5051` (Database: `qa_audit_db`)
   - Documents Service: `http://localhost:5171` (Database: `qa_document_db`)
   - React Frontend: `http://localhost:3000`
2. **Environment Configuration**:
   Copy `.env.example` to `.env.e2e.local` (which is excluded by `.gitignore`) in `tests/selenium/` and supply QA test account credentials:
   ```properties
   E2E_BASE_URL=http://localhost:3000
   E2E_TENANT_NAME="SE3112 E2E Testing Agency"

   # Baseline Role Accounts
   E2E_OWNER_EMAIL=owner.qa@example.com
   E2E_OWNER_PASSWORD=<qa_owner_password>
   E2E_STAFF_EMAIL=staff.qa@example.com
   E2E_STAFF_PASSWORD=<qa_staff_password>
   E2E_CLIENT_EMAIL=client.qa@example.com
   E2E_CLIENT_PASSWORD=<qa_client_password>

   # Dedicated Stage 2 Fixture Client (APPR01, DOC01, DOC02)
   E2E_STAGE2_CLIENT_EMAIL=stage2.client.qa@example.com
   E2E_STAGE2_CLIENT_PASSWORD=<qa_stage2_client_password>
   ```

3. **Dedicated QA Fixtures**:
   - **Baseline Engagement (Stage 1 Draft)**: `2767cc0a-edf6-4143-bf80-1f4cc2a3a065`
   - **Dedicated Stage 2 Engagement**: `4ca380d3-04e0-4be5-8e6a-6f8a760803b6`
   - **Verified Closed Engagement**: `015bfe04-cb20-44c5-8be0-ccb4be30b18f`

4. **GitHub Actions Secrets Configuration**:
   For automated CI pipelines, configure the following 8 repository secrets in **Settings → Secrets and variables → Actions**:
   - `E2E_OWNER_EMAIL`
   - `E2E_OWNER_PASSWORD`
   - `E2E_STAFF_EMAIL`
   - `E2E_STAFF_PASSWORD`
   - `E2E_CLIENT_EMAIL`
   - `E2E_CLIENT_PASSWORD`
   - `E2E_STAGE2_CLIENT_EMAIL`
   - `E2E_STAGE2_CLIENT_PASSWORD`

---

## 3. Running the Tests

### A. Academic Viva Quick Demo (Recommended for 2-Minute Presentation)
Executes the CSTD-25 Client Approval Workflow in **~60 seconds** with visible Chrome and deliberate visual pacing:
```powershell
.\tests\selenium\run-demo.ps1 -QuickDemo
```

### B. Demonstration Runner (Visible Chrome, All Workflows)
Executes all 11 routine tests sequentially in visible Chrome:
```powershell
.\tests\selenium\run-demo.ps1 -DelayMs 600
```

### C. Headless CI Mode (Fast Execution, <90s)
Executes all 11 tests sequentially in headless mode (suitable for GitHub Actions):
```powershell
.\tests\selenium\run-demo.ps1 -Headless
```

### D. Running Specific Workflow Tests
```powershell
# Run CSTD-25 Approval Workflow
.\tests\selenium\run-demo.ps1 -Filter "APPR01"

# Run Document Rejection & Verification
.\tests\selenium\run-demo.ps1 -Filter "DOC02"

# Run SLA Stall Queue & Intervention
.\tests\selenium\run-demo.ps1 -Filter "STALL01"

# Run Cryptographic Hash Chain & PDF Report Validation
.\tests\selenium\run-demo.ps1 -Filter "AUDIT01|REPORT01"
```

### E. Running via dotnet test Directly
```powershell
$env:DOTNET_ROLL_FORWARD = "Major"

# Run all CI-category tests headless
$env:SELENIUM_HEADLESS = "true"
dotnet test .\tests\selenium\Custodian.System.Tests\Custodian.System.Tests.csproj `
    --filter "Category=CI" `
    --logger "console;verbosity=normal"
```

---

## 4. Diagnostic Screenshots & Reliability

- **Automatic Failure Screenshots**: When any test fails, an automated high-resolution diagnostic screenshot is captured to:
  `tests/selenium/Custodian.System.Tests/bin/Debug/net10.0/Screenshots/`
- **Zero Credential Bleed**: Log messages and failure reports strictly suppress tokens, cookies, and passwords.
- **Session Isolation**: Every multi-role test uses `SwitchToFreshIndependentBrowserSession()` to cleanly terminate the browser process and prevent token bleed between Owner, Staff, and Client roles.

---

## 5. Security & Safe CI Execution

### A. Unconditional Production Endpoint Protection
The default deployed Azure App Services (`custodian-*-prod-gr`) and Vercel frontend target live Azure MySQL production databases (`identity_db`, `workflow_db`, `document_db`, `audit_db`).

To protect production data, the CI pipeline enforces an **unconditional hard block**:
- If target endpoints match known production hosts (`custodian-blue.vercel.app` or `*-prod-*`), state-changing Selenium browser execution is **permanently disabled**.
- Confirmation flags (`E2E_QA_ISOLATION_CONFIRMED` or `confirm_qa_isolation`) **CANNOT override** this hard block while production endpoints are targeted.
- Safe compilation (`dotnet build`) and test tier analysis continue to validate every commit and PR.

### B. Configuring an Isolated QA Environment for Future Execution
To run E2E browser tests in CI after an isolated QA environment has been deployed:
1. Define non-production QA endpoint variables in GitHub (**Settings → Secrets and variables → Actions → Variables**):
   - `E2E_QA_BASE_URL` (e.g., `https://qa.custodian.app` or staging URL)
   - `E2E_QA_IDENTITY_URL`, `E2E_QA_WORKFLOW_URL`, `E2E_QA_DOCUMENTS_URL`, `E2E_QA_AUDIT_URL`
2. Confirm database isolation by setting repository variable `E2E_QA_ISOLATION_CONFIRMED='true'` (or passing `confirm_qa_isolation='true'` via `workflow_dispatch`).
3. Ensure the 8 required GitHub Secrets are populated.

### C. Pull Request Security & Protected Environments
- **No Secret Exposure to PR Code**: The browser test step is guarded with `if: github.event_name != 'pull_request'`, ensuring runner environments never evaluate or inject secrets during pull requests (protecting against malicious code in forks or untrusted branches).
- **Compilation Gate**: Pull requests run `dotnet build` in Release mode and test tier selection, ensuring complete build verification without exposing credentials.
- **Protected Environment**: The pipeline binds to GitHub Environment `e2e-tests`, enabling maintainers to enforce environment protection rules (such as Required Reviewers).
- **Never Use `pull_request_target`**: The workflow strictly uses standard `on: pull_request`.

### D. Credential Rotation Runbook
If persistent test credentials are ever rotated:
1. **Rotate in Identity Database**: An authorized administrator updates passwords for the persistent QA accounts (`E2E_OWNER_EMAIL`, `E2E_STAFF_EMAIL`, `E2E_CLIENT_EMAIL`, and `E2E_STAGE2_CLIENT_EMAIL`) in the Identity database.
2. **Update GitHub Repository Secrets**: Update the corresponding `E2E_*_PASSWORD` secrets in GitHub (**Settings → Secrets and variables → Actions**).
3. **Update Local Developer Config**: Team members update their local `tests/selenium/.env.e2e.local` files.
4. **Audit Tracked Code**: Verify zero passwords, tokens, or plaintext secrets are ever committed to Git.
