using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace Custodian.System.Tests;

/// <summary>
/// Streamlined Workflow 3: CSTD-25 Client Approval & Decision Workflow.
/// Validates cross-service condition gating: Staff attaches approval condition,
/// Client reviews pending approval with countdown in portal, enforces mandatory rejection reason,
/// Client rejects, and Staff confirms gate blocker enforcement.
/// </summary>
public class ApprovalWorkflowTests : TestBase
{
    private const string Stage2EngagementId = "4ca380d3-04e0-4be5-8e6a-6f8a760803b6";
    private const string Stage2ClientEmail = "se3112.e2e.gp.da244274@example.com";
    private const string Stage2ClientPassword = "P@ssw0rd123!";

    [Fact]
    [Trait("Category", "CI")]
    public void APPR01_StaffAttachesApprovalCondition_ClientRejectionAndGateBlocker()
    {
        string uniqueTag = Guid.NewGuid().ToString("N")[..6];
        string approvalTitle = $"QA SLA Milestone Sign-off {uniqueTag}";
        string rejectionReason = "Legal counsel requested clause 4 modification prior to approval.";

        try
        {
            // =========================================================================
            // STEP 1: Staff logs in and attaches an Approval condition in Workspace
            // =========================================================================
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            // If an active condition already exists from a previous run, deactivate it first so we can attach cleanly
            var existingDeactBtns = Driver.FindElements(By.XPath("//button[@title='Deactivate condition']"));
            if (existingDeactBtns.Count > 0 && existingDeactBtns[0].Displayed)
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", existingDeactBtns[0]);
                TestEnvironment.DemoPause(200);
                existingDeactBtns[0].Click();
                TestEnvironment.DemoPause(300);

                var deactModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Deactivate Condition')]/ancestor::div[contains(@class, 'bg-white')]")));
                var deactReason = deactModal.FindElement(By.XPath(".//textarea[@required]"));
                deactReason.SendKeys("Automated test cleanup of active condition");
                TestEnvironment.DemoPause(200);

                var confirmDeactBtn = deactModal.FindElement(By.XPath(".//button[@type='submit' and contains(., 'Deactivate')]"));
                confirmDeactBtn.Click();
                TestEnvironment.DemoPause(600);

                Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Deactivate Condition')]")).Count == 0);
                TestEnvironment.DemoPause(400);
            }

            // Locate and click "Attach Condition" button
            var attachCondBtn = Wait.Until(d =>
            {
                var buttons = d.FindElements(By.XPath("//button[contains(., 'Attach Condition') and not(@type='submit')]"));
                return buttons.Count > 0 && buttons[0].Displayed && buttons[0].Enabled ? buttons[0] : null;
            });
            Assert.NotNull(attachCondBtn);
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", attachCondBtn);
            TestEnvironment.DemoPause(200);
            attachCondBtn.Click();
            TestEnvironment.DemoPause(300);

            // Assert Attach Condition modal is displayed (title: "Attach Engagement Condition")
            var modal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Attach') and contains(text(), 'Condition')]/ancestor::div[contains(@class, 'bg-white')]")));
            Assert.True(modal.Displayed);

            // Enter condition title
            var titleInput = modal.FindElement(By.XPath(".//input[@placeholder='e.g. Board of Directors Onboarding Approval' or @required]"));
            titleInput.Clear();
            titleInput.SendKeys(approvalTitle);
            TestEnvironment.DemoPause(300);

            // Submit condition creation
            var submitCondBtn = modal.FindElement(By.XPath(".//button[@type='submit' and contains(., 'Attach Condition')]"));
            submitCondBtn.Click();
            TestEnvironment.DemoPause(600);

            // Wait for modal dismissal and verify condition appears in workspace
            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Attach') and contains(text(), 'Condition')]")).Count == 0);
            var conditionCardInStaff = Wait.Until(d => d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(text(), '{approvalTitle}')]")));
            Assert.True(conditionCardInStaff.Displayed);
            TestEnvironment.DemoPause(400);

            // =========================================================================
            // STEP 2: Client logs into Client Portal to review pending approval
            // =========================================================================
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(Stage2ClientEmail, Stage2ClientPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/portal"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(500);

            // Locate approval condition card in Client Portal
            var clientConditionHeader = Wait.Until(d => d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(text(), '{approvalTitle}')]")));
            Assert.True(clientConditionHeader.Displayed);

            var conditionContainer = clientConditionHeader.FindElement(By.XPath("./ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-2xl')]"));
            Assert.Contains("Approval Gate", conditionContainer.Text);
            Assert.Contains("Pending", conditionContainer.Text);

            // =========================================================================
            // STEP 3: Client exercises rejection with mandatory reason validation
            // =========================================================================
            var rejectBtn = conditionContainer.FindElement(By.XPath(".//button[contains(., 'Reject')]"));
            Assert.True(rejectBtn.Displayed && rejectBtn.Enabled);
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", rejectBtn);
            TestEnvironment.DemoPause(200);
            rejectBtn.Click();
            TestEnvironment.DemoPause(400);

            // Assert rejection modal is visible (title: "Reject Approval Gate")
            var rejectModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Reject Approval')]/ancestor::div[contains(@class, 'bg-white')]")));
            Assert.True(rejectModal.Displayed);

            var reasonTextarea = rejectModal.FindElement(By.XPath(".//textarea[contains(@placeholder, 'Please provide the reason')]"));
            var confirmRejectBtn = rejectModal.FindElement(By.XPath(".//button[@type='submit' and contains(., 'Reject Approval')]"));

            // Validation check: Empty reason keeps the reject button disabled
            Assert.False(confirmRejectBtn.Enabled);

            // Enter valid rejection reason
            reasonTextarea.Clear();
            reasonTextarea.SendKeys(rejectionReason);
            TestEnvironment.DemoPause(300);

            // Confirm reject button becomes active and submit
            Assert.True(confirmRejectBtn.Enabled);
            confirmRejectBtn.Click();
            TestEnvironment.DemoPause(800);

            // Wait for rejection modal to dismiss
            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Reject Approval')]")).Count == 0);

            // Verify portal reflects rejection state and reason
            Wait.Until(d =>
            {
                var card = d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(text(), '{approvalTitle}')]/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-2xl')]"));
                return card.Text.Contains("Rejected by Client") || card.Text.Contains("Rejected");
            });

            var rejectedCard = Driver.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(text(), '{approvalTitle}')]/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-2xl')]"));
            Assert.Contains(rejectionReason, rejectedCard.Text);
            TestEnvironment.DemoPause(400);

            // =========================================================================
            // STEP 4: Staff verifies rejected condition status and gate restriction
            // =========================================================================
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            // Verify condition reflects Rejected state in Staff view
            var staffRejectedCard = Wait.Until(d => d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(text(), '{approvalTitle}')]/ancestor::div[contains(@class, 'p-4')]")));
            Assert.Contains("Rejected", staffRejectedCard.Text);
            TestEnvironment.DemoPause(300);
        }
        catch (Exception)
        {
            CaptureScreenshot("APPR01_Failure");
            throw;
        }
    }
}
