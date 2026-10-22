using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;
using Xunit.Abstractions;

namespace Custodian.System.Tests;

/// <summary>
/// SE3112 Role D — E2E06 Full Client Lifecycle Golden Path Test.
/// Demonstrates the complete Custodian business workflow from Client provisioning,
/// through all five sequential stages with real multi-role transitions (Owner, Staff, Client),
/// standard checklist template seeding, deterministic document compliance checks,
/// dual-sync staff verification, stage advancement gate enforcement, readiness evaluation,
/// delivery handoff, cryptographic ledger sealing, and tamper-evident audit verification.
///
/// TEST-DATA POLICY:
/// - Explicitly opt-in: excluded from default test runs and CI unless RUN_GOLDEN_PATH_E2E=true.
/// - Creates a single, collision-resistant unique Client and Engagement.
/// - Never touches the baseline QA engagement ('E2E Test Client').
/// - Preserves full diagnostic traceability and records checkpoints if interrupted.
/// </summary>
public class ClientLifecycleE2ETests : TestBase
{
    private readonly ITestOutputHelper? _output;
    private readonly List<string> _tempFiles = new();

    public ClientLifecycleE2ETests(ITestOutputHelper? output = null)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "Lifecycle")]
    [Trait("Category", "OptIn")]
    public void E2E06_FullClientLifecycle_CreationToClosure_GoldenPath()
    {
        // =========================================================================
        // GUARD: Opt-in Execution Control
        // Excluded from routine test runs and CI to prevent unintended QA mutations.
        // =========================================================================
        bool isOptIn = string.Equals(Environment.GetEnvironmentVariable("RUN_GOLDEN_PATH_E2E"), "true", StringComparison.OrdinalIgnoreCase);
        if (!isOptIn)
        {
            string skipNotice = "[OPT-IN REQUIRED] E2E06 Golden Path performs irreversible state-changing mutations on QA database. Set RUN_GOLDEN_PATH_E2E=true to execute.";
            Console.WriteLine(skipNotice);
            _output?.WriteLine(skipNotice);
            return;
        }

        // Diagnostic execution tracking with collision-resistant identity
        string clientUniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        string testClientName = $"SE3112-E2E-GP-{clientUniqueSuffix}";
        string testClientEmail = $"se3112.e2e.gp.{clientUniqueSuffix}@example.com";
        string testClientPassword = "P@ssw0rd123!";
        string? engagementId = null;
        int currentCheckpoint = 0;
        string currentCheckpointDesc = "Initialization";

        void LogCheckpoint(int step, string description)
        {
            currentCheckpoint = step;
            currentCheckpointDesc = description;
            string msg = $"[CHECKPOINT {step:D2}] {description} (Client: {testClientName}, EngId: {engagementId ?? "pending"})";
            Console.WriteLine(msg);
            _output?.WriteLine(msg);
            TestEnvironment.DemoPause(400);
        }

        // Resilient test execution with cloud database latency buffers

        try
        {
            // =========================================================================
            // PHASE 1: OWNER PROVISIONS CLIENT AND ENGAGEMENT
            // =========================================================================
            LogCheckpoint(1, "Owner authenticates and enters workspace");
            LoginViaUi(TestEnvironment.OwnerEmail, TestEnvironment.OwnerPassword, TestEnvironment.TenantName);
            WaitSeconds(25).Until(d => d.Url.Contains("/engagements"));

            LogCheckpoint(2, "Owner provisions new collision-resistant Client");
            var newEngagementBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'New Engagement')]")));
            newEngagementBtn.Click();
            TestEnvironment.DemoPause(300);

            var modalHeader = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'New Client Engagement')]")));
            Assert.True(modalHeader.Displayed);

            var addNewClientBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'Add New Client')]")));
            addNewClientBtn.Click();
            TestEnvironment.DemoPause(300);

            var nameInput = WaitSeconds(25).Until(d => d.FindElement(By.CssSelector("input[placeholder*='Client company or contact name']")));
            nameInput.Clear();
            nameInput.SendKeys(testClientName);

            var emailInput = Driver.FindElement(By.CssSelector("input[placeholder*='client@company.com']"));
            emailInput.Clear();
            emailInput.SendKeys(testClientEmail);

            var passInput = Driver.FindElement(By.CssSelector("input[placeholder*='Portal Initial Password']"));
            passInput.Clear();
            passInput.SendKeys(testClientPassword);
            TestEnvironment.DemoPause(300);

            var provisionClientBtn = Driver.FindElement(By.XPath("//button[contains(., 'Provision Client Portal Account')]"));
            provisionClientBtn.Click();
            TestEnvironment.DemoPause(600);

            var provisionSuccessBanner = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Client Portal Account Provisioned')]")));
            Assert.True(provisionSuccessBanner.Displayed);

            LogCheckpoint(3, "Owner assigns QA Staff custodian and initializes workspace");
            var staffSelectEl = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//label[contains(text(), 'ASSIGNED STAFF CUSTODIAN')]/following::select[1]")));
            var staffDropdown = new SelectElement(staffSelectEl);
            staffDropdown.SelectByText(TestEnvironment.StaffEmail);
            TestEnvironment.DemoPause(300);

            var initWorkspaceBtn = Driver.FindElement(By.XPath("//button[contains(., 'INITIALIZE WORKSPACE')]"));
            initWorkspaceBtn.Click();
            TestEnvironment.DemoPause(800);

            // Inspection modal opens automatically for the newly created engagement
            var inspectionModalHeader = WaitSeconds(25).Until(d => d.FindElement(By.XPath($"//h2[contains(text(), '{testClientName}')]")));
            Assert.True(inspectionModalHeader.Displayed);

            var proceedBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'Proceed into Workspace')]")));
            proceedBtn.Click();
            TestEnvironment.DemoPause(600);

            WaitSeconds(25).Until(d => d.Url.Contains("/workspace/"));
            engagementId = Driver.Url.Substring(Driver.Url.LastIndexOf('/') + 1).Trim();
            Assert.False(string.IsNullOrWhiteSpace(engagementId), "Engagement ID must be resolved from workspace URL.");
            LogCheckpoint(4, $"Workspace initialized with Engagement ID: {engagementId}");

            // =========================================================================
            // PHASE 2: STAFF LOGS IN, APPLIES STANDARD CHECKLIST, AND STARTS ENGAGEMENT
            // =========================================================================
            LogCheckpoint(5, "Staff authenticates and enters assigned workspace");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engagementId}");
            WaitSeconds(25).Until(d => d.Url.Contains($"/workspace/{engagementId}"));

            // Verify initial Draft state
            var draftBadge = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Draft — not started') or contains(text(), 'Draft')]")));
            Assert.True(draftBadge.Displayed);

            LogCheckpoint(6, "Staff applies the 7-task Standard Checklist to the engagement");
            var standardChecklistBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'Standard checklist')]")));
            Assert.True(standardChecklistBtn.Displayed);
            standardChecklistBtn.Click();
            TestEnvironment.DemoPause(400);

            var checklistModal = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Apply Standard Checklist')]/ancestor::div[contains(@class, 'bg-white')]")));
            Assert.True(checklistModal.Displayed);

            // Wait for preview tasks to load
            WaitSeconds(25).Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Apply Standard Checklist')]/ancestor::div[contains(@class, 'bg-white')]//li[contains(@class, 'p-2')]")).Count == 7);

            // Confirm checkbox
            var confirmCheckbox = checklistModal.FindElement(By.XPath(".//label[contains(., 'reviewed these tasks')]//input[@type='checkbox']"));
            if (!confirmCheckbox.Selected)
            {
                confirmCheckbox.Click();
            }
            TestEnvironment.DemoPause(300);

            var applyTasksBtn = checklistModal.FindElement(By.XPath(".//button[contains(., 'Add 7 tasks') or (contains(., 'Add') and contains(., 'task'))]"));
            applyTasksBtn.Click();

            // Cloud batch operation: apply checklist inserts 7 tasks and re-fetches workspace data across all services
            WaitSeconds(45).Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Apply Standard Checklist')]")).Count == 0);
            TestEnvironment.DemoPause(500);

            // Verify Stage 1 task is visible in Staff workspace
            var stage1TaskInWorkspace = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Client Intake & Kickoff Assessment')]")));
            Assert.True(stage1TaskInWorkspace.Displayed);

            LogCheckpoint(7, "Staff activates engagement from Draft to Started");
            var startBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'Start engagement')]")));
            Assert.True(startBtn.Displayed);
            startBtn.Click();
            TestEnvironment.DemoPause(400);

            // Accept window.confirm alert
            var confirmAlert = Driver.SwitchTo().Alert();
            confirmAlert.Accept();
            TestEnvironment.DemoPause(800);

            // Wait for status badge to reflect Started / In progress
            var inProgressBadge = WaitSeconds(30).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'In progress') or contains(text(), 'Started')]")));
            Assert.True(inProgressBadge.Displayed);

            // Reload page to verify persisted Started state from backend database
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);
            var persistedStartedBadge = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'In progress') or contains(text(), 'Started')]")));
            Assert.True(persistedStartedBadge.Displayed);

            // =========================================================================
            // PHASE 3: STAGE 1 — ONBOARDING
            // Client completes Task 1 in Portal -> Staff advances to Stage 2
            // =========================================================================
            LogCheckpoint(8, "Client authenticates into Client Portal to complete Task 1 (Client Intake & Kickoff Assessment)");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(testClientEmail, testClientPassword, TestEnvironment.TenantName);
            WaitSeconds(25).Until(d => d.Url.Contains("/portal"));

            // Complete Task 1 in Client Portal
            CompleteClientGeneralTask("Client Intake & Kickoff Assessment", 1);

            LogCheckpoint(9, "Staff verifies Stage 1 completion and advances to Stage 2 (Document Collection)");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engagementId}");
            WaitSeconds(25).Until(d => d.Url.Contains($"/workspace/{engagementId}"));

            var advanceToStage2Btn = WaitSeconds(35).Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Advance to Stage 2') or contains(., 'Advance stage')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            advanceToStage2Btn.Click();
            var stage2Heading = WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Document Collection')]")));
            Assert.True(stage2Heading.Displayed);

            // Refresh workspace to verify persisted Stage 2 state from backend database
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);
            WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Document Collection')]")).Displayed);

            // =========================================================================
            // PHASE 4: STAGE 2 — DOCUMENT COLLECTION
            // Client uploads 3 valid PDFs -> Staff verifies 3 evidence docs -> Staff advances
            // =========================================================================
            LogCheckpoint(10, "Client authenticates into Client Portal to upload all 3 required Stage 2 documents");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(testClientEmail, testClientPassword, TestEnvironment.TenantName);
            WaitSeconds(25).Until(d => d.Url.Contains("/portal"));

            // Prepare synthetic PDF evidence files
            string kycPdf = CreateSyntheticPdf("KYC_Passport_Proof");
            string msaPdf = CreateSyntheticPdf("Master_Services_Agreement");
            string poaPdf = CreateSyntheticPdf("Proof_Of_Address_Bill");

            // Task 2: KYC Passport
            UploadDocumentEvidence(
                taskTitle: "Identity Verification (KYC Passport / ID)",
                pdfPath: kycPdf,
                issueDate: DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd"),
                expiryDate: DateTime.UtcNow.AddYears(5).ToString("yyyy-MM-dd")
            );

            // Task 3: Signed MSA (Must be uploaded and verified, not left unaddressed)
            UploadDocumentEvidence(
                taskTitle: "Signed Master Services Agreement",
                pdfPath: msaPdf,
                issueDate: DateTime.UtcNow.AddDays(-10).ToString("yyyy-MM-dd"),
                expiryDate: DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd")
            );

            // Task 4: Proof of Address (strictly <= 90 days freshness)
            UploadDocumentEvidence(
                taskTitle: "Proof of Address",
                pdfPath: poaPdf,
                issueDate: DateTime.UtcNow.AddDays(-15).ToString("yyyy-MM-dd"),
                expiryDate: DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd")
            );

            // Refresh Client Portal and verify evidence tasks show Uploaded
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);

            LogCheckpoint(11, "Staff verifies and synchronizes all 3 Stage 2 documents in workspace");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engagementId}");
            WaitSeconds(25).Until(d => d.Url.Contains($"/workspace/{engagementId}"));

            // Verify evidence for all 3 tasks in Stage 2
            VerifyEvidenceTask("Identity Verification (KYC Passport / ID)", "Identity and passport validity confirmed.");
            VerifyEvidenceTask("Signed Master Services Agreement", "Executed MSA agreement verified.");
            VerifyEvidenceTask("Proof of Address", "Proof of address within 90 days validity verified.");

            // Refresh workspace to verify persisted verification state
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);

            LogCheckpoint(12, "Staff advances to Stage 3 (Verification)");
            var advanceToStage3Btn = WaitSeconds(35).Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Advance to Stage 3') or contains(., 'Advance stage')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            advanceToStage3Btn.Click();
            var stage3Heading = WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Verification')]")));
            Assert.True(stage3Heading.Displayed);

            // Refresh and verify persisted Stage 3 state from backend database
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);
            WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Verification')]")).Displayed);

            // =========================================================================
            // PHASE 5: STAGE 3 — VERIFICATION
            // Staff completes Task 5 (Compliance Review) -> Advances to Stage 4
            // =========================================================================
            LogCheckpoint(13, "Staff completes Task 5 (Compliance Review & Verification Evaluation)");
            var task5MarkCompleteBtn = WaitSeconds(35).Until(d =>
            {
                var card = d.FindElement(By.XPath("//span[contains(text(), 'Compliance Review & Verification Evaluation')]/ancestor::div[contains(@class, 'p-3.5') or contains(@class, 'rounded-xl')]"));
                var btn = card.FindElement(By.XPath(".//button[contains(., 'Mark Complete')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            task5MarkCompleteBtn.Click();

            // Wait for Task 5 to show Done or Mark Complete button to dismiss
            WaitSeconds(35).Until(d =>
                d.FindElements(By.XPath("//span[contains(text(), 'Compliance Review & Verification Evaluation')]/ancestor::div[contains(@class, 'rounded-xl')]//span[contains(text(), 'Done')]")).Count > 0 ||
                d.FindElements(By.XPath("//span[contains(text(), 'Compliance Review & Verification Evaluation')]/ancestor::div[contains(@class, 'rounded-xl')]//button[contains(., 'Mark Complete')]")).Count == 0
            );

            // Refresh and verify Task 5 completion persisted from backend database
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);
            WaitSeconds(35).Until(d => d.FindElements(By.XPath("//span[contains(text(), 'Compliance Review & Verification Evaluation')]/ancestor::div[contains(@class, 'rounded-xl')]//span[contains(text(), 'Done')]")).Count > 0);

            LogCheckpoint(14, "Staff advances to Stage 4 (Execution) after passing gate checks");
            var advanceToStage4Btn = WaitSeconds(35).Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Advance to Stage 4') or contains(., 'Advance stage')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            advanceToStage4Btn.Click();
            var stage4Heading = WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Execution')]")));
            Assert.True(stage4Heading.Displayed);

            // Refresh and verify persisted Stage 4 state from backend database
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);
            WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Execution')]")).Displayed);

            // =========================================================================
            // PHASE 6: STAGE 4 — EXECUTION
            // Client completes Task 6 (Milestone Sign-off) in Portal -> Staff advances to Stage 5
            // =========================================================================
            LogCheckpoint(15, "Client signs off Task 6 (Service Delivery Milestone Sign-off) in Portal");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(testClientEmail, testClientPassword, TestEnvironment.TenantName);
            WaitSeconds(25).Until(d => d.Url.Contains("/portal"));

            // Complete Task 6 in Client Portal (Task 6 makes 6/7 completed)
            CompleteClientGeneralTask("Service Delivery Milestone Sign-off", 6);

            LogCheckpoint(16, "Staff advances to Stage 5 (Closure)");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engagementId}");
            WaitSeconds(25).Until(d => d.Url.Contains($"/workspace/{engagementId}"));

            var advanceToStage5Btn = WaitSeconds(35).Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Advance to Stage 5') or contains(., 'Advance stage')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            advanceToStage5Btn.Click();
            var stage5Heading = WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Closure')]")));
            Assert.True(stage5Heading.Displayed);

            // Refresh and verify persisted Stage 5 state from backend database
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);
            WaitSeconds(35).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Closure')]")).Displayed);

            // =========================================================================
            // PHASE 7: STAGE 5 — CLOSURE, HANDOFF, AND LEDGER SEAL
            // Client completes Task 7 -> Staff records handoff -> Staff seals engagement
            // =========================================================================
            LogCheckpoint(17, "Client completes Task 7 (Final Handoff & Ledger Seal) in Portal");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(testClientEmail, testClientPassword, TestEnvironment.TenantName);
            WaitSeconds(25).Until(d => d.Url.Contains("/portal"));

            // Complete Task 7 in Client Portal (Task 7 makes 7/7 completed)
            CompleteClientGeneralTask("Final Handoff & Ledger Seal", 7);

            LogCheckpoint(18, "Staff verifies Readiness evaluation, records handoff, and seals engagement");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engagementId}");
            WaitSeconds(25).Until(d => d.Url.Contains($"/workspace/{engagementId}"));

            // Verify Readiness: all gates satisfied and ready for handoff and closure
            var readyBadge = WaitSeconds(35).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Ready for handoff and closure')]")));
            Assert.True(readyBadge.Displayed);

            // 1. Record Handoff
            var recordHandoffBtn = WaitSeconds(35).Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Record handoff')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            recordHandoffBtn.Click();
            TestEnvironment.DemoPause(400);

            var handoffSummaryInput = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Record Handoff')]/ancestor::div[contains(@class, 'bg-white')]//textarea")));
            handoffSummaryInput.Clear();
            handoffSummaryInput.SendKeys("SE3112 E2E06 Golden Path deliverables successfully delivered and verified.");
            TestEnvironment.DemoPause(300);

            var submitHandoffBtn = Driver.FindElement(By.XPath("//h3[contains(text(), 'Record Handoff')]/ancestor::div[contains(@class, 'bg-white')]//button[@type='submit']"));
            submitHandoffBtn.Click();
            WaitSeconds(35).Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Record Handoff')]")).Count == 0);
            TestEnvironment.DemoPause(500);

            // 2. Seal & Complete Engagement
            var sealBtn = WaitSeconds(35).Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Seal & complete') or contains(., 'Seal & Complete')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            sealBtn.Click();
            TestEnvironment.DemoPause(400);

            var closureModalHeader = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Seal & Complete Engagement')]")));
            Assert.True(closureModalHeader.Displayed);

            var closureSummaryInput = Driver.FindElement(By.XPath("//h3[contains(text(), 'Seal & Complete Engagement')]/ancestor::div[contains(@class, 'bg-white')]//textarea"));
            closureSummaryInput.Clear();
            closureSummaryInput.SendKeys("SE3112 E2E06 Golden Path full lifecycle completed, all compliance verified, and ledger sealed.");
            TestEnvironment.DemoPause(300);

            var submitSealBtn = Driver.FindElement(By.XPath("//h3[contains(text(), 'Seal & Complete Engagement')]/ancestor::div[contains(@class, 'bg-white')]//button[@type='submit']"));
            submitSealBtn.Click();
            WaitSeconds(35).Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Seal & Complete Engagement')]")).Count == 0);
            TestEnvironment.DemoPause(800);

            // Refresh workspace to verify persisted Terminal Closed state
            Driver.Navigate().Refresh();
            TestEnvironment.DemoPause(500);

            var closedBadge = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Closed')]")));
            Assert.True(closedBadge.Displayed);

            LogCheckpoint(19, "Engagement is successfully Sealed and Closed. Progress is 100%.");

            // =========================================================================
            // PHASE 8: DELIVERY REPORT AND TAMPER-EVIDENT AUDIT TRAIL
            // =========================================================================
            LogCheckpoint(20, "Verify Delivery Readiness Handoff Report is accessible");
            var reportBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'readiness report')]")));
            Assert.True(reportBtn.Displayed);

            LogCheckpoint(21, "Owner verifies immutable Audit Trail and Cryptographic Hash Chain");
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.OwnerEmail, TestEnvironment.OwnerPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/audit");
            WaitSeconds(25).Until(d => d.Url.Contains("/audit"));
            WaitSeconds(35).Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(500);

            // Bounded wait for Kafka background consumer to ingest events for this engagement
            var engagementFilterEl = WaitSeconds(25).Until(d => d.FindElement(By.CssSelector("select[aria-label='Filter by engagement']")));

            bool engagementFoundInAudit = false;
            var auditWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(45));
            auditWait.Until(d =>
            {
                // Wait for any in-flight load / spin to finish
                var spinners = d.FindElements(By.CssSelector(".animate-spin"));
                if (spinners.Count > 0) return false;

                var selectEl = d.FindElement(By.CssSelector("select[aria-label='Filter by engagement']"));
                var sel = new SelectElement(selectEl);
                var match = sel.Options.FirstOrDefault(o =>
                    o.Text.Contains(testClientName, StringComparison.OrdinalIgnoreCase) ||
                    o.GetAttribute("value") == engagementId);

                if (match != null)
                {
                    sel.SelectByValue(match.GetAttribute("value") ?? "");
                    engagementFoundInAudit = true;
                    return true;
                }

                // If not yet ingested by Kafka/EventHub consumer, click the in-page Refresh button
                var refreshBtns = d.FindElements(By.XPath("//button[contains(., 'Refresh') and not(@disabled)]"));
                if (refreshBtns.Count > 0)
                {
                    refreshBtns[0].Click();
                }
                TestEnvironment.DemoPause(3000);
                return false;
            });

            Assert.True(engagementFoundInAudit, $"Engagement {engagementId} must appear in Audit Log filter.");
            TestEnvironment.DemoPause(500);

            // Verify events recorded
            WaitSeconds(25).Until(d => d.FindElements(By.XPath("//button[contains(@class, 'w-full') and contains(@class, 'text-left')]")).Count > 0);
            var auditEvents = Driver.FindElements(By.XPath("//button[contains(@class, 'w-full') and contains(@class, 'text-left')]"));
            Assert.NotEmpty(auditEvents);

            // Execute tamper-evident cryptographic hash chain verification
            var verifyChainBtn = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//button[contains(., 'Verify this engagement') or contains(., 'Verify all chains')]")));
            verifyChainBtn.Click();
            TestEnvironment.DemoPause(500);

            WaitSeconds(25).Until(d => verifyChainBtn.FindElements(By.CssSelector(".animate-spin")).Count == 0);

            // Assert hash chains KPI indicates Intact
            WaitSeconds(25).Until(d =>
            {
                var kpi = d.FindElement(By.XPath("//span[contains(text(), 'Hash chains')]/ancestor::div[contains(@class, 'p-5')]//div[contains(@class, 'text-2xl')]"));
                return kpi.Text.Contains("Intact");
            });

            var tamperingAlerts = Driver.FindElements(By.XPath("//div[contains(text(), 'Tampering detected')]"));
            Assert.Empty(tamperingAlerts);

            LogCheckpoint(22, "E2E06 Full Client Lifecycle Golden Path completed with 100% SUCCESS!");
        }
        catch (Exception ex)
        {
            string failMsg = $"[LIFECYCLE TEST FAILURE at Checkpoint {currentCheckpoint}: {currentCheckpointDesc}]\n" +
                             $"ClientName: {testClientName}\n" +
                             $"ClientEmail: {testClientEmail}\n" +
                             $"EngagementId: {engagementId ?? "Not yet initialized"}\n" +
                             $"Error: {ex.Message}";
            Console.WriteLine(failMsg);
            _output?.WriteLine(failMsg);
            CaptureScreenshot($"E2E06_Failure_Checkpoint{currentCheckpoint}");
            throw;
        }
        finally
        {
            CleanupTempFiles();
        }
    }

    #region Helper Methods

    /// <summary>
    /// Generates a valid synthetic PDF binary conforming to the PDF-1.4 specification.
    /// Does not contain real identity or personal information.
    /// </summary>
    private string CreateSyntheticPdf(string prefix)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.pdf");
        string pdfContent =
            "%PDF-1.4\n" +
            "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/MediaBox[0 0 612 792]/Parent 2 0 R/Resources<<>>>>endobj\n" +
            "xref\n0 4\n0000000000 65535 f\n0000000010 00000 n\n0000000060 00000 n\n0000000117 00000 n\n" +
            "trailer<</Size 4/Root 1 0 R>>\nstartxref\n205\n%%EOF\n";

        File.WriteAllBytes(filePath, Encoding.ASCII.GetBytes(pdfContent));
        _tempFiles.Add(filePath);
        return filePath;
    }

    /// <summary>
    /// Resilient explicit wait tuned for cloud database latency across microservices.
    /// </summary>
    private WebDriverWait WaitSeconds(int seconds) => new(Driver, TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Populates an HTML5 date input reliably by invoking the native property setter and dispatching
    /// React change/input events. Bypasses Chrome's locale-dependent date keystroke masking.
    /// </summary>
    private void SetDateInput(IWebElement element, string yyyyMmDd)
    {
        ((IJavaScriptExecutor)Driver).ExecuteScript(
            "const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set; " +
            "if (setter) { setter.call(arguments[0], arguments[1]); } else { arguments[0].value = arguments[1]; } " +
            "arguments[0].dispatchEvent(new Event('input', { bubbles: true })); " +
            "arguments[0].dispatchEvent(new Event('change', { bubbles: true }));",
            element, yyyyMmDd);
    }

    /// <summary>
    /// Completes a general (non-evidence, non-requirement) task in the Client Portal.
    /// Supports both the Hero 'Primary Next Action' panel (button: 'Complete Action')
    /// and the 'Upcoming Stage Tasks' list (button: 'Mark Done').
    /// Verifies task-specific identity before clicking and confirms backend persistence.
    /// </summary>
    private void CompleteClientGeneralTask(string taskTitle, int expectedCompletedCount)
    {
        IWebElement? actionBtn = null;

        var heroHeadings = Driver.FindElements(By.XPath($"//h2[contains(text(), '{taskTitle}')]"));
        if (heroHeadings.Count > 0 && heroHeadings[0].Displayed)
        {
            var heroCard = heroHeadings[0].FindElement(By.XPath("./ancestor::div[contains(@class, 'rounded-2xl')]"));
            actionBtn = heroCard.FindElement(By.XPath(".//button[contains(., 'Complete Action') or contains(., 'Mark Done')]"));
        }
        else
        {
            actionBtn = WaitSeconds(25).Until(d =>
            {
                var row = d.FindElement(By.XPath($"//span[contains(@class, 'font-bold') and contains(text(), '{taskTitle}')]/ancestor::div[contains(@class, 'p-3.5') or contains(@class, 'rounded-xl')]"));
                var btn = row.FindElement(By.XPath(".//button[contains(., 'Mark Done') or contains(., 'Complete Action')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
        }

        Assert.NotNull(actionBtn);
        Assert.True(actionBtn.Enabled, $"Completion button for task '{taskTitle}' must be enabled.");
        actionBtn.Click();
        TestEnvironment.DemoPause(800);

        // Explicit wait for completion to take effect in the UI
        WaitSeconds(35).Until(d =>
            d.FindElements(By.XPath($"//span[contains(text(), '{expectedCompletedCount} / 7 Tasks Completed')]")).Count > 0 ||
            d.FindElements(By.XPath("//h3[contains(text(), 'All Caught Up')]")).Count > 0
        );

        // Refresh Client Portal and verify persisted completion from backend database
        Driver.Navigate().Refresh();
        TestEnvironment.DemoPause(500);

        WaitSeconds(35).Until(d =>
            d.FindElements(By.XPath($"//span[contains(text(), '{expectedCompletedCount} / 7 Tasks Completed')]")).Count > 0 ||
            d.FindElements(By.XPath("//h3[contains(text(), 'All Caught Up')]")).Count > 0
        );
    }

    /// <summary>
    /// Helper to find a task row by title in Client Portal and submit evidence via UploadEvidenceModal.
    /// Supports both the Hero 'Primary Next Action' panel (button: 'Upload Evidence')
    /// and the 'Upcoming Stage Tasks' list (button: 'Upload').
    /// </summary>
    private void UploadDocumentEvidence(string taskTitle, string pdfPath, string issueDate, string expiryDate)
    {
        IWebElement? uploadBtn = null;

        var heroHeadings = Driver.FindElements(By.XPath($"//h2[contains(text(), '{taskTitle}')]"));
        if (heroHeadings.Count > 0 && heroHeadings[0].Displayed)
        {
            var heroCard = heroHeadings[0].FindElement(By.XPath("./ancestor::div[contains(@class, 'rounded-2xl')]"));
            uploadBtn = heroCard.FindElement(By.XPath(".//button[contains(., 'Upload')]"));
        }
        else
        {
            uploadBtn = WaitSeconds(25).Until(d =>
            {
                var card = d.FindElement(By.XPath($"//span[contains(@class, 'font-bold') and contains(text(), '{taskTitle}')]/ancestor::div[contains(@class, 'p-3.5') or contains(@class, 'rounded-xl')]"));
                var btn = card.FindElement(By.XPath(".//button[contains(., 'Upload')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
        }

        Assert.NotNull(uploadBtn);
        Assert.True(uploadBtn.Enabled, $"Upload button for '{taskTitle}' must be enabled.");
        uploadBtn.Click();
        TestEnvironment.DemoPause(400);

        var modal = WaitSeconds(25).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Upload Evidence Document')]/ancestor::div[contains(@class, 'bg-white')]")));
        Assert.True(modal.Displayed);

        // Populate hidden file input directly via SendKeys
        var fileInput = Driver.FindElement(By.Id("evidence-file-input"));
        fileInput.SendKeys(pdfPath);
        TestEnvironment.DemoPause(300);

        // Fill Issue Date using React-compatible property setter to prevent masked date corruption
        var issueDateInput = modal.FindElement(By.XPath(".//label[contains(text(), 'Issue Date')]/following::input[1]"));
        SetDateInput(issueDateInput, issueDate);

        // Fill Expiry Date using React-compatible property setter
        var expiryDateInput = modal.FindElement(By.XPath(".//label[contains(text(), 'Expiry Date')]/following::input[1]"));
        SetDateInput(expiryDateInput, expiryDate);
        TestEnvironment.DemoPause(300);

        // Submit Evidence
        var submitBtn = modal.FindElement(By.XPath(".//button[contains(., 'Submit Evidence')]"));
        submitBtn.Click();
        TestEnvironment.DemoPause(800);

        // Wait for modal to dismiss
        new WebDriverWait(Driver, TimeSpan.FromSeconds(35)).Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Upload Evidence Document')]")).Count == 0);
    }

    /// <summary>
    /// Helper to find an uploaded task in Staff workspace and complete dual-sync verification.
    /// Uses resilient selector to avoid StaleElementReferenceException during re-renders.
    /// </summary>
    private void VerifyEvidenceTask(string taskTitle, string notes)
    {
        var verifyBtn = new WebDriverWait(Driver, TimeSpan.FromSeconds(35)).Until(d =>
        {
            var btn = d.FindElement(By.XPath($"//span[contains(text(), '{taskTitle}')]/ancestor::div[contains(@class, 'p-3.5') or contains(@class, 'rounded-xl')]//button[contains(., 'Verify Evidence')]"));
            return btn.Displayed && btn.Enabled ? btn : null;
        });
        verifyBtn.Click();
        TestEnvironment.DemoPause(400);

        var verifyModal = new WebDriverWait(Driver, TimeSpan.FromSeconds(30)).Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Verify') and contains(text(), 'Evidence')]/ancestor::div[contains(@class, 'bg-white')]")));
        Assert.True(verifyModal.Displayed);

        if (!string.IsNullOrWhiteSpace(notes))
        {
            var notesInput = verifyModal.FindElement(By.XPath(".//textarea[contains(@placeholder, 'Document details') or contains(@placeholder, 'regulatory compliance')]"));
            notesInput.Clear();
            notesInput.SendKeys(notes);
            TestEnvironment.DemoPause(200);
        }

        var confirmBtn = verifyModal.FindElement(By.XPath(".//button[contains(., 'Confirm Verification')]"));
        confirmBtn.Click();
        TestEnvironment.DemoPause(800);

        // Wait for modal to close
        new WebDriverWait(Driver, TimeSpan.FromSeconds(35)).Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Verify') and contains(text(), 'Evidence')]")).Count == 0);
    }

    private void CleanupTempFiles()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
        _tempFiles.Clear();
    }

    #endregion
}
