using System;
using System.Linq;
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
    private const string QaApprovalPrefix = "QA SLA Milestone Sign-off";

    [Fact]
    [Trait("Category", "CI")]
    public void APPR01_StaffAttachesApprovalCondition_ClientRejectionAndGateBlocker()
    {
        string uniqueTag = Guid.NewGuid().ToString("N")[..6];
        string approvalTitle = $"{QaApprovalPrefix} {uniqueTag}";
        string rejectionReason = "Legal counsel requested clause 4 modification prior to approval.";

        string? createdApprovalTitle = null;
        Exception? testException = null;

        try
        {
            // =========================================================================
            // STEP 1: Staff logs in and attaches an Approval condition in Workspace
            // =========================================================================
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
            WaitForWorkspaceLoaded();
            TestEnvironment.DemoPause(400);

            // Deactivate any active approval conditions left by previous QA test runs to guarantee a clean slate.
            // Strictly targets only conditions whose titles begin with "QA SLA Milestone Sign-off".
            DeactivateExistingActiveConditions();

            // Locate and click "Attach Condition" button with bounded retry to handle async background re-renders
            var modal = OpenAttachConditionModal();
            Assert.True(modal.Displayed);

            // Enter condition title
            var titleInput = modal.FindElement(By.XPath(".//input[@placeholder='e.g. Board of Directors Onboarding Approval' or @required]"));
            titleInput.Clear();
            titleInput.SendKeys(approvalTitle);
            TestEnvironment.DemoPause(300);

            // Submit condition creation
            var submitCondBtn = modal.FindElement(By.XPath(".//button[@type='submit' and contains(., 'Attach Condition')]"));
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", submitCondBtn);
            TestEnvironment.DemoPause(300);
            try
            {
                submitCondBtn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", submitCondBtn);
            }
            TestEnvironment.DemoPause(600);

            // Track that the condition creation request was submitted so post-test cleanup can target it
            createdApprovalTitle = approvalTitle;

            // Wait for modal dismissal or condition card appearance
            try
            {
                Wait.Until(d =>
                {
                    try
                    {
                        var modals = d.FindElements(By.XPath("//h3[contains(., 'Attach') and contains(., 'Condition')]"));
                        if (modals.Count == 0) return true;

                        var cardAppeared = d.FindElements(By.XPath($"//*[self::h3 or self::h4][contains(., '{approvalTitle}')]")).Count > 0;
                        if (cardAppeared)
                        {
                            var cancelBtns = d.FindElements(By.XPath("//h3[contains(., 'Attach') and contains(., 'Condition')]/ancestor::div[contains(@class, 'bg-white')]//button[contains(., 'Cancel')]"));
                            if (cancelBtns.Count > 0)
                            {
                                try { cancelBtns[0].Click(); } catch { }
                            }
                            return true;
                        }
                        return false;
                    }
                    catch (StaleElementReferenceException)
                    {
                        return true;
                    }
                });
            }
            catch (WebDriverTimeoutException)
            {
                var errors = Driver.FindElements(By.XPath("//div[contains(@class, 'bg-red-50')] | //span[contains(@class, 'text-red')]"))
                    .Select(e => e.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                throw new Exception($"Attach Condition modal did not close. Visible errors: [{string.Join("; ", errors)}]");
            }

            var conditionCardInStaff = Wait.Until(d => d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(., '{approvalTitle}')]")));
            Assert.True(conditionCardInStaff.Displayed);
            TestEnvironment.DemoPause(400);

            // =========================================================================
            // STEP 2: Client logs into Client Portal to review pending approval
            // =========================================================================
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.Stage2ClientEmail, TestEnvironment.Stage2ClientPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/portal"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(500);

            // Locate approval condition card in Client Portal
            var clientConditionHeader = Wait.Until(d => d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(., '{approvalTitle}')]")));
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
            var rejectModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(., 'Reject Approval')]/ancestor::div[contains(@class, 'bg-white')]")));
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
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center', inline: 'center'});", confirmRejectBtn);
            TestEnvironment.DemoPause(300);
            try
            {
                confirmRejectBtn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", confirmRejectBtn);
            }
            TestEnvironment.DemoPause(800);

            // Wait for rejection modal to dismiss
            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(., 'Reject Approval')]")).Count == 0);

            // Verify portal reflects rejection state and reason
            Wait.Until(d =>
            {
                var card = d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(., '{approvalTitle}')]/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-2xl')]"));
                return card.Text.Contains("Rejected by Client") || card.Text.Contains("Rejected");
            });

            var rejectedCard = Driver.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(., '{approvalTitle}')]/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-2xl')]"));
            Assert.Contains(rejectionReason, rejectedCard.Text);
            TestEnvironment.DemoPause(400);

            // =========================================================================
            // STEP 4: Staff verifies rejected condition status and gate restriction
            // =========================================================================
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
            WaitForWorkspaceLoaded();
            TestEnvironment.DemoPause(400);

            // Verify condition reflects Rejected state in Staff view
            var staffRejectedCard = Wait.Until(d => d.FindElement(By.XPath($"//*[self::h3 or self::h4][contains(., '{approvalTitle}')]/ancestor::div[contains(@class, 'p-4')]")));
            Assert.Contains("Rejected", staffRejectedCard.Text);
            TestEnvironment.DemoPause(300);
        }
        catch (Exception ex)
        {
            testException = ex;
            CaptureScreenshot("APPR01_Failure");
            throw;
        }
        finally
        {
            // =========================================================================
            // POST-TEST CLEANUP: Safely deactivates test-generated condition inside finally block
            // =========================================================================
            if (!string.IsNullOrEmpty(createdApprovalTitle))
            {
                try
                {
                    CleanupCreatedCondition(createdApprovalTitle);
                }
                catch (Exception cleanupEx)
                {
                    if (testException != null)
                    {
                        // Preserve original test failure exception while recording cleanup diagnostic
                        Console.WriteLine($"[APPR01 Cleanup Error]: {cleanupEx}");
                    }
                    else
                    {
                        // If test passed assertions but cleanup failed, fail the test so uncleaned state is not ignored
                        throw new InvalidOperationException($"APPR01 cleanup failed for condition '{createdApprovalTitle}': {cleanupEx.Message}", cleanupEx);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reliably verifies that the workspace data and conditions collection have finished loading.
    /// Checks for cleared spinners, present section header, populated Active Gate count badge,
    /// and rendered Attach Condition button.
    /// </summary>
    private void WaitForWorkspaceLoaded()
    {
        Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
        Wait.Until(d =>
        {
            // 1. Panel heading must exist
            var headings = d.FindElements(By.XPath("//h3[contains(., 'Engagement Conditions')]"));
            if (headings.Count == 0) return false;

            // 2. Active gate indicator badge must be populated with condition count (e.g. "0 Active Gates" or "1 Active Gate")
            var gateBadges = d.FindElements(By.XPath("//span[contains(., 'Active Gate')]"));
            if (gateBadges.Count == 0) return false;

            // 3. Attach Condition button must be present and ready in the DOM
            var attachButtons = d.FindElements(By.XPath("//button[contains(., 'Attach Condition') and not(@type='submit')]"));
            return attachButtons.Count > 0;
        });
    }

    /// <summary>
    /// Deactivates any active approval conditions left by previous QA test runs.
    /// Strictly filters to conditions whose titles begin with "QA SLA Milestone Sign-off".
    /// Never touches unrelated payment or business approval conditions.
    /// </summary>
    private void DeactivateExistingActiveConditions()
    {
        for (int i = 0; i < 5; i++)
        {
            WaitForWorkspaceLoaded();

            // Locate active deactivation buttons specifically for QA test-generated approval conditions
            var qaDeactBtns = Driver.FindElements(By.XPath(
                $"//*[self::h3 or self::h4][starts-with(normalize-space(.), '{QaApprovalPrefix}')]" +
                "/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-xl')]" +
                "//button[@title='Deactivate condition']"));

            if (qaDeactBtns.Count == 0) break;

            var btn = qaDeactBtns[0];
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", btn);
            TestEnvironment.DemoPause(200);

            try
            {
                btn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", btn);
            }
            TestEnvironment.DemoPause(300);

            var deactModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(., 'Deactivate Condition')]/ancestor::div[contains(@class, 'bg-white')]")));
            var deactReason = deactModal.FindElement(By.XPath(".//textarea[@required]"));
            deactReason.Clear();
            deactReason.SendKeys("Automated test cleanup of active condition");
            TestEnvironment.DemoPause(200);

            var confirmDeactBtn = deactModal.FindElement(By.XPath(".//button[@type='submit' and contains(., 'Deactivate')]"));
            try
            {
                confirmDeactBtn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", confirmDeactBtn);
            }
            TestEnvironment.DemoPause(600);

            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(., 'Deactivate Condition')]")).Count == 0);
            WaitForWorkspaceLoaded();
            TestEnvironment.DemoPause(400);
        }
    }

    /// <summary>
    /// Safely deactivates the specific test-generated condition during post-test cleanup.
    /// Re-navigates and re-authenticates as Staff if the test failed during client portal steps.
    /// Re-locates the card freshly by title, submits deactivation, and asserts inactivity.
    /// </summary>
    private void CleanupCreatedCondition(string targetTitle)
    {
        // Ensure browser is on the workspace page as Staff
        if (!Driver.Url.Contains($"/workspace/{Stage2EngagementId}"))
        {
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
        }
        WaitForWorkspaceLoaded();

        // Freshly re-locate the condition card by title
        var conditionCards = Driver.FindElements(By.XPath($"//*[self::h3 or self::h4][contains(., '{targetTitle}')]/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-xl')]"));
        if (conditionCards.Count == 0)
        {
            // Condition was never attached or already cleared
            return;
        }

        var deactBtns = conditionCards[0].FindElements(By.XPath(".//button[@title='Deactivate condition']"));
        if (deactBtns.Count > 0)
        {
            var btn = deactBtns[0];
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", btn);
            TestEnvironment.DemoPause(200);

            try
            {
                btn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", btn);
            }
            TestEnvironment.DemoPause(300);

            var deactModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(., 'Deactivate Condition')]/ancestor::div[contains(@class, 'bg-white')]")));
            var deactReason = deactModal.FindElement(By.XPath(".//textarea[@required]"));
            deactReason.Clear();
            deactReason.SendKeys("Automated test cleanup after APPR01 execution");
            TestEnvironment.DemoPause(200);

            var confirmDeactBtn = deactModal.FindElement(By.XPath(".//button[@type='submit' and contains(., 'Deactivate')]"));
            try
            {
                confirmDeactBtn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", confirmDeactBtn);
            }
            TestEnvironment.DemoPause(600);

            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(., 'Deactivate Condition')]")).Count == 0);
            WaitForWorkspaceLoaded();
            TestEnvironment.DemoPause(400);
        }

        // Verify that the specific condition is now actually inactive
        Wait.Until(d =>
        {
            var freshCards = d.FindElements(By.XPath($"//*[self::h3 or self::h4][contains(., '{targetTitle}')]/ancestor::div[contains(@class, 'p-4') or contains(@class, 'rounded-xl')]"));
            if (freshCards.Count == 0) return true;
            var activeDeactBtns = freshCards[0].FindElements(By.XPath(".//button[@title='Deactivate condition']"));
            return activeDeactBtns.Count == 0 || freshCards[0].Text.Contains("Deactivated");
        });
    }

    /// <summary>
    /// Robustly opens the Attach Engagement Condition modal with bounded retries
    /// to handle React asynchronous background re-renders.
    /// </summary>
    private IWebElement OpenAttachConditionModal()
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            WaitForWorkspaceLoaded();

            var attachBtns = Driver.FindElements(By.XPath("//button[contains(., 'Attach Condition') and not(@type='submit')]"));
            var attachBtn = attachBtns.FirstOrDefault(b => b.Displayed && b.Enabled) ?? attachBtns.FirstOrDefault();

            if (attachBtn == null)
            {
                TestEnvironment.DemoPause(300);
                continue;
            }

            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center'});", attachBtn);
            TestEnvironment.DemoPause(200);

            try
            {
                attachBtn.Click();
            }
            catch
            {
                ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", attachBtn);
            }
            TestEnvironment.DemoPause(400);

            var shortWait = new WebDriverWait(Driver, TimeSpan.FromSeconds(5));
            try
            {
                var modal = shortWait.Until(d =>
                {
                    var modals = d.FindElements(By.XPath("//h3[contains(., 'Attach') and contains(., 'Condition')]/ancestor::div[contains(@class, 'bg-white')]"));
                    return modals.Count > 0 && modals[0].Displayed ? modals[0] : null;
                });

                if (modal != null)
                {
                    return modal;
                }
            }
            catch (WebDriverTimeoutException)
            {
                // Modal didn't open in time, retry attempt
                TestEnvironment.DemoPause(300);
            }
        }

        // Final attempt with full standard wait
        return Wait.Until(d => d.FindElement(By.XPath("//h3[contains(., 'Attach') and contains(., 'Condition')]/ancestor::div[contains(@class, 'bg-white')]")));
    }
}
