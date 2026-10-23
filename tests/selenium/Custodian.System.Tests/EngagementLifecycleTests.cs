using System;
using System.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace Custodian.System.Tests;

/// <summary>
/// Streamlined Workflow 2: Engagement Provisioning, Staff Assignment, and Stage-Gate Enforcement.
/// Validates cross-service workflow initiation (Identity client provisioning + Workflow engagement initialization),
/// multi-role assignment persistence, and strict Next Action stage-gate blocker enforcement.
/// </summary>
public class EngagementLifecycleTests : TestBase
{
    private const string BaselineEngagementId = "2767cc0a-edf6-4143-bf80-1f4cc2a3a065";

    [Fact]
    [Trait("Category", "CI")]
    public void LIFE01_Owner_CreatesEngagementAndAssignsStaff_WithSelfCleanup()
    {
        try
        {
            // 1. Log in as QA Owner and enter workspace
            LoginViaUi(TestEnvironment.OwnerEmail, TestEnvironment.OwnerPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/engagements"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(300);

            // 2. Exercise live dashboard search filter to confirm record filtering works
            var searchInput = Wait.Until(d => d.FindElement(By.CssSelector("input[placeholder*='Search by client']")));
            searchInput.Clear();
            searchInput.SendKeys("E2E Test Client");
            TestEnvironment.DemoPause(300);

            var matchingCards = Wait.Until(d => d.FindElements(By.XPath("//div[contains(@class, 'group') and .//h3[contains(text(), 'E2E Test Client')]]")));
            Assert.NotEmpty(matchingCards);

            searchInput.SendKeys(Keys.Control + "a");
            searchInput.SendKeys(Keys.Backspace);
            TestEnvironment.DemoPause(300);

            // 3. Open 'New Engagement' Modal
            var newEngagementBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'New Engagement')]")));
            newEngagementBtn.Click();
            TestEnvironment.DemoPause(300);

            var modalHeader = Wait.Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'New Client Engagement')]")));
            Assert.True(modalHeader.Displayed);

            // 4. Provision collision-resistant client account
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string testClientName = $"SE3112-E2E-Client-{timestamp}";
            string testClientEmail = $"se3112.client.{timestamp}@example.com";
            string testClientPass = "P@ssw0rd123!";

            var addNewClientBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Add New Client')]")));
            addNewClientBtn.Click();
            TestEnvironment.DemoPause(300);

            var nameInput = Wait.Until(d => d.FindElement(By.CssSelector("input[placeholder*='Client company or contact name']")));
            nameInput.Clear();
            nameInput.SendKeys(testClientName);

            var emailInput = Driver.FindElement(By.CssSelector("input[placeholder*='client@company.com']"));
            emailInput.Clear();
            emailInput.SendKeys(testClientEmail);

            var passInput = Driver.FindElement(By.CssSelector("input[placeholder*='Portal Initial Password']"));
            passInput.Clear();
            passInput.SendKeys(testClientPass);
            TestEnvironment.DemoPause(300);

            var provisionClientBtn = Driver.FindElement(By.XPath("//button[contains(., 'Provision Client Portal Account')]"));
            provisionClientBtn.Click();
            TestEnvironment.DemoPause(500);

            var provisionSuccessBanner = Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Client Portal Account Provisioned')]")));
            Assert.True(provisionSuccessBanner.Displayed);

            // 5. Assign existing QA Staff account
            // Wait for team members API to load and the configured QA Staff option to be present in the dropdown
            try
            {
                Wait.Until(d =>
                {
                    var selectEl = d.FindElement(By.XPath("//label[contains(text(), 'ASSIGNED STAFF CUSTODIAN')]/following::select[1]"));
                    var select = new SelectElement(selectEl);
                    return select.Options.Any(opt => string.Equals(opt.Text.Trim(), TestEnvironment.StaffEmail, StringComparison.OrdinalIgnoreCase));
                });
            }
            catch (WebDriverTimeoutException)
            {
                var selectEl = Driver.FindElement(By.XPath("//label[contains(text(), 'ASSIGNED STAFF CUSTODIAN')]/following::select[1]"));
                var select = new SelectElement(selectEl);
                var loadedOptionCount = select.Options.Count;
                var loadedOptionsDiagnostics = loadedOptionCount == 1 && select.Options[0].Text.Contains("Lead Custodian")
                    ? "Team members list failed to load from API (only default 'Me (Lead Custodian)' option present)."
                    : $"Team members list loaded ({loadedOptionCount} options), but configured QA Staff account was not found among options.";
                throw new InvalidOperationException($"Failed to locate configured QA Staff option in Assigned Staff dropdown: {loadedOptionsDiagnostics}");
            }

            var staffDropdown = new SelectElement(Driver.FindElement(By.XPath("//label[contains(text(), 'ASSIGNED STAFF CUSTODIAN')]/following::select[1]")));
            staffDropdown.SelectByText(TestEnvironment.StaffEmail);
            TestEnvironment.DemoPause(300);

            // 6. Initialize workspace
            var initWorkspaceBtn = Driver.FindElement(By.XPath("//button[contains(., 'INITIALIZE WORKSPACE')]"));
            initWorkspaceBtn.Click();
            TestEnvironment.DemoPause(600);

            var inspectionModalHeader = Wait.Until(d => d.FindElement(By.XPath($"//h2[contains(text(), '{testClientName}')]")));
            Assert.True(inspectionModalHeader.Displayed);

            var cancelBtn = Driver.FindElement(By.XPath("//div[contains(@class, 'justify-between')]//button[contains(text(), 'Cancel')]"));
            cancelBtn.Click();
            TestEnvironment.DemoPause(300);

            // 7. Switch session to Staff (zero cookie bleed) and verify assignment
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/engagements"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);

            var staffCard = Wait.Until(d => d.FindElement(By.XPath($"//h3[contains(text(), '{testClientName}')]")));
            Assert.True(staffCard.Displayed);

            var staffCardContainer = staffCard.FindElement(By.XPath("./ancestor::div[contains(@class, 'group')]"));
            Assert.Contains("Draft", staffCardContainer.Text);
            TestEnvironment.DemoPause(400);

            // 8. Safe Test-Data Cleanup: Staff opens the draft engagement and deletes it cleanly via UI
            staffCard.Click();
            TestEnvironment.DemoPause(400);

            var deleteBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Delete Engagement')]")));
            Assert.True(deleteBtn.Displayed);
            deleteBtn.Click();
            TestEnvironment.DemoPause(300);

            // Accept window.confirm alert
            var alert = Driver.SwitchTo().Alert();
            alert.Accept();
            TestEnvironment.DemoPause(500);

            // Verify clean removal
            Wait.Until(d => d.FindElements(By.XPath($"//h3[contains(text(), '{testClientName}')]")).Count == 0);
        }
        catch (Exception)
        {
            CaptureScreenshot("LIFE01_Failure");
            throw;
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public void LIFE02_Staff_StageGateRestrictionAndReadinessBlockers()
    {
        try
        {
            // 1. Authenticate with real QA Staff credentials
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/engagements"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);

            // 2. Open baseline draft engagement workspace directly
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{BaselineEngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{BaselineEngagementId}"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            // 3. Assert active stage and Draft state
            var clientHeader = Wait.Until(d => d.FindElement(By.XPath("//h1[contains(text(), 'E2E Test Client')]")));
            Assert.True(clientHeader.Displayed);

            var draftBadge = Driver.FindElement(By.XPath("//span[contains(text(), 'Draft — not started') or contains(text(), 'Draft')]"));
            Assert.True(draftBadge.Displayed);

            // 4. Inspect readiness and next-action blockers
            var nextActionHeading = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Next Action')]")));
            Assert.True(nextActionHeading.Displayed);

            var primaryAction = Wait.Until(d => d.FindElement(By.XPath("//section[@aria-label='Next action']//div[contains(text(), 'Activate engagement')]")));
            Assert.True(primaryAction.Displayed);

            var readinessStatus = Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Engagement not started')]")));
            Assert.True(readinessStatus.Displayed);

            // 5. Verify stage advancement is strictly disabled due to genuine blockers
            var advanceBlockedHint = Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Resolve 1 open item before advancing.')]")));
            Assert.True(advanceBlockedHint.Displayed);

            var advanceBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Advance to Stage 2: Document Collection')]")));
            Assert.True(advanceBtn.Displayed);
            Assert.Equal("true", advanceBtn.GetAttribute("disabled"));
            Assert.False(advanceBtn.Enabled);

            string buttonTitle = advanceBtn.GetAttribute("title") ?? "";
            Assert.Contains("Resolve 1 open item before advancing.", buttonTitle);
            TestEnvironment.DemoPause(300);
        }
        catch (Exception)
        {
            CaptureScreenshot("LIFE02_Failure");
            throw;
        }
    }
}
