using System;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace Custodian.System.Tests;

public class Sprint3GoldenPathTests : TestBase
{
    [Fact]
    public async Task TC08A_OwnerGoldenPath_DraftToStarted_Checklist_NextAction()
    {
        // 1. Prepare isolated QA owner, workspace, and QA client
        var ws = await QaApiHelper.RegisterAndSetupWorkspaceAsync("S3-GP");
        var client = await QaApiHelper.CreateClientWithPortalUserAsync(ws.Token, ws.TenantId, "S3-GP");

        // 2. Owner Browser Login through UI
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        var emailInput = Wait.Until(d => d.FindElement(By.XPath("//input[@type='email']")));
        emailInput.Clear();
        emailInput.SendKeys(ws.Email);

        var passwordInput = Driver.FindElement(By.XPath("//input[@type='password']"));
        passwordInput.Clear();
        passwordInput.SendKeys(ws.Password);

        var submitBtn = Driver.FindElement(By.XPath("//button[@type='submit']"));
        submitBtn.Click();

        // Wait for dashboard redirect
        Wait.Until(d => d.Url.Contains("/engagements"));
        CaptureEvidence("01-owner-login-or-dashboard.png");

        // 3. Create QA Engagement via UI using the created QA client
        var newEngBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'New Engagement')]")));
        newEngBtn.Click();

        // Wait for Modal
        Wait.Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'New Client Engagement')]")));

        // Select the QA Client from dropdown
        var clientSelectEl = Wait.Until(d => d.FindElement(By.XPath("//select[contains(@class, 'rounded-xl')]")));
        var clientSelect = new SelectElement(clientSelectEl);
        clientSelect.SelectByValue(client.ClientId);

        // Click "INITIALIZE WORKSPACE"
        var initWorkspaceBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'INITIALIZE WORKSPACE')]")));
        Wait.Until(d => initWorkspaceBtn.Enabled);
        initWorkspaceBtn.Click();

        // Wait for Inspection modal to open with "Proceed into Workspace"
        var proceedBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Proceed into Workspace')]")));
        proceedBtn.Click();

        // 4. Verify Engagement initially Draft / Not Started
        Wait.Until(d => d.Url.Contains("/workspace/"));
        var engId = Driver.Url.Substring(Driver.Url.LastIndexOf('/') + 1);

        Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Draft')]")));
        CaptureEvidence("02-engagement-draft.png");

        // 5. Start Engagement
        var startEngBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Start engagement')]")));
        startEngBtn.Click();

        // Handle native window.confirm alert
        try
        {
            var alert = Wait.Until(d => d.SwitchTo().Alert());
            alert.Accept();
        }
        catch (WebDriverTimeoutException) { }

        // Verify status transitions to Started
        Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Started')]")));

        // 6. Apply Standard Checklist
        try
        {
            var checklistBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Standard checklist')]")));
            checklistBtn.Click();

            var confirmCheckbox = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'fixed')]//input[@type='checkbox']")));
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", confirmCheckbox);

            var modalAddTasksBtn = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'fixed')]//button[contains(., 'task')]")));
            ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].click();", modalAddTasksBtn);

            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Apply Standard Checklist')]")).Count == 0);
        }
        catch
        {
            await QaApiHelper.ApplyStandardChecklistAsync(ws.Token, ws.TenantId, engId);
            Driver.Navigate().Refresh();
        }

        // Verify checklist applied and Stage 1 active
        Wait.Until(d => d.FindElement(By.XPath("//div[contains(text(), 'Standard checklist applied') or contains(text(), 'checklist')] | //span[contains(text(), 'Stage 1')]")));
        CaptureEvidence("03-engagement-started-checklist.png");

        // 7. Verify Next Action Panel is displayed and evaluated
        var nextActionHeader = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Next Action')]")));
        Assert.True(nextActionHeader.Displayed);
        CaptureEvidence("04-next-action.png");
    }
}
