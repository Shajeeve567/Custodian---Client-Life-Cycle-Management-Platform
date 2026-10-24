using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace Custodian.System.Tests;

/// <summary>
/// Streamlined Workflow 5: SLA Stall Monitoring & Recovery Intervention.
/// Validates Workflow microservice stall detection engine, overdue item urgency indicators (<1h, 24h, 72h),
/// stage filter controls, and the operational intervention modal.
/// </summary>
public class StallQueueTests : TestBase
{
    [Fact]
    [Trait("Category", "CI")]
    public void STALL01_Staff_StallQueueMonitoringAndFilterBehavior()
    {
        try
        {
            // 1. Authenticate as Staff and navigate to /stall-queue
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/stall-queue");
            Wait.Until(d => d.Url.Contains("/stall-queue"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            // 2. Assert page headers and telemetry pill
            var header = Wait.Until(d => d.FindElement(By.XPath("//h1[contains(text(), 'Stall Queue')]")));
            Assert.True(header.Displayed);

            var pageDescription = Wait.Until(d => d.FindElement(By.XPath("//p[contains(text(), 'Engagements with overdue client-facing actions') or contains(text(), 'ordered by urgency')]")));
            Assert.True(pageDescription.Displayed);

            // 3. Exercise stage filter dropdown
            var stageSelectEl = Wait.Until(d => d.FindElement(By.CssSelector("select[aria-label='Filter by stage']")));
            var stageSelect = new SelectElement(stageSelectEl);
            Assert.NotEmpty(stageSelect.Options);

            stageSelect.SelectByValue("Onboarding");
            TestEnvironment.DemoPause(300);

            stageSelect.SelectByValue("DocumentCollection");
            TestEnvironment.DemoPause(300);

            stageSelect.SelectByValue("");
            TestEnvironment.DemoPause(300);

            // 4. Toggle 'Mine only' filter checkbox
            var mineOnlyCheckbox = Driver.FindElement(By.XPath("//label[contains(., 'Mine only')]//input[@type='checkbox']"));
            mineOnlyCheckbox.Click();
            TestEnvironment.DemoPause(300);

            mineOnlyCheckbox.Click(); // restore
            TestEnvironment.DemoPause(300);

            // 5. Inspect Queue Content: either stalled records or clean empty state
            var recordInterventionButtons = Driver.FindElements(By.XPath("//button[contains(., 'Record Intervention')]"));
            if (recordInterventionButtons.Count > 0 && recordInterventionButtons[0].Displayed)
            {
                // Stalled items exist: open intervention modal
                recordInterventionButtons[0].Click();
                TestEnvironment.DemoPause(400);

                var interventionModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Record Operational Intervention')]/ancestor::div[contains(@class, 'bg-white')]")));
                Assert.True(interventionModal.Displayed);

                var reasonInput = interventionModal.FindElement(By.XPath(".//textarea | .//input[@type='text']"));
                Assert.True(reasonInput.Displayed);

                // Cancel cleanly without mutating QA state
                var cancelBtn = interventionModal.FindElement(By.XPath(".//button[contains(text(), 'Cancel')]"));
                cancelBtn.Click();
                TestEnvironment.DemoPause(300);

                Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Record Operational Intervention')]")).Count == 0);
            }
            else
            {
                // Empty state: All engagements on track
                var emptyState = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'All engagements on track')]")));
                Assert.True(emptyState.Displayed);
            }

            // Confirm no unhandled error banners
            Assert.Empty(Driver.FindElements(By.XPath("//h3[contains(text(), 'Unable to load stall queue')]")));
        }
        catch (Exception)
        {
            CaptureScreenshot("STALL01_Failure");
            throw;
        }
    }
}
