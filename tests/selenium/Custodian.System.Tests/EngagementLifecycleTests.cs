using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class EngagementLifecycleTests : TestBase
{
    [Fact]
    public void TC05_EngagementView_ShouldRenderHeaderAndNewButton()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Ensure we are on Engagements tab
        var engTab = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Engagements')]")));
        engTab.Click();

        var header = Wait.Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'Engagement Lifecycle Management')]")));
        var newBtn = Driver.FindElement(By.XPath("//button[contains(., 'New Engagement')]"));

        Assert.True(header.Displayed);
        Assert.True(newBtn.Displayed);
    }

    [Fact]
    public void TC06_NewEngagementModal_ShouldOpenAndFillInputs()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Open Modal
        var newBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'New Engagement')]")));
        newBtn.Click();

        // Check Modal rendered
        var modalTitle = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Initiate New Client Engagement')]")));
        Assert.True(modalTitle.Displayed);

        // Interact with Client ID and Staff ID inputs
        var clientIdInput = Driver.FindElement(By.XPath("//label[contains(text(), 'Client ID:')]/following-sibling::input"));
        clientIdInput.Clear();
        clientIdInput.SendKeys("cli-qa-test-101");

        var staffIdInput = Driver.FindElement(By.XPath("//label[contains(text(), 'Assigned Staff ID:')]/following-sibling::input"));
        staffIdInput.Clear();
        staffIdInput.SendKeys("stf-qa-test-202");

        // Click Cancel to close modal cleanly
        var cancelBtn = Driver.FindElement(By.XPath("//button[contains(text(), 'Cancel')]"));
        cancelBtn.Click();

        // Verify modal backdrop disappears
        var modals = Driver.FindElements(By.ClassName("modal-backdrop"));
        Assert.Empty(modals);
    }
}

