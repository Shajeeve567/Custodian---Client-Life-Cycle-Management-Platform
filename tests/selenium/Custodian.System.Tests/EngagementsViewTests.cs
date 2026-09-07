using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class EngagementsViewTests : TestBase
{
    [Fact]
    public void TC06_EngagementsDashboard_SearchAndFilters()
    {
        // Authenticate into workspace session
        InjectMockAuthSession("Owner", "tenant-alpha", "Alpha Agency");
        Driver.Navigate().GoToUrl($"{BaseUrl}/engagements");

        // Verify Workspace Layout Header
        var brandBadge = Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Custodian OS')]")));
        Assert.True(brandBadge.Displayed);

        // Verify Search Input functionality
        var searchInput = Wait.Until(d => d.FindElement(By.XPath("//input[contains(@placeholder, 'Search by client')]")));
        searchInput.Clear();
        searchInput.SendKeys("Global Logistics");
        Assert.Equal("Global Logistics", searchInput.GetAttribute("value"));

        // Verify Status Filter Tabs (All, Started, Closed)
        var startedFilter = Driver.FindElement(By.XPath("//button[contains(text(), 'Started')]"));
        startedFilter.Click();
        Assert.Contains("text-indigo-700", startedFilter.GetAttribute("class"));

        var allFilter = Driver.FindElement(By.XPath("//button[contains(text(), 'All Engagements')]"));
        allFilter.Click();
        Assert.Contains("text-indigo-700", allFilter.GetAttribute("class"));
    }

    [Fact]
    public void TC07_NewEngagementModal_ShouldOpenAndCloseCleanly()
    {
        InjectMockAuthSession("Owner", "tenant-alpha", "Alpha Agency");
        Driver.Navigate().GoToUrl($"{BaseUrl}/engagements");

        // Click 'New Engagement' CTA
        var newEngagementBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'New Engagement')]")));
        newEngagementBtn.Click();

        // Verify Modal Opens with 'PROVISION PROTOCOL' & 'New Client Engagement'
        var modalHeader = Wait.Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'New Client Engagement')]")));
        var protocolBadge = Driver.FindElement(By.XPath("//span[contains(text(), 'PROVISION PROTOCOL')]"));
        Assert.True(modalHeader.Displayed);
        Assert.True(protocolBadge.Displayed);

        // Find and click the Close (X) button inside the modal header
        var closeBtn = Driver.FindElement(By.XPath("//h2[contains(text(), 'New Client Engagement')]/ancestor::div[contains(@class, 'justify-between')]//button"));
        closeBtn.Click();

        // Verify modal closes cleanly without page errors
        Wait.Until(d => d.FindElements(By.XPath("//h2[contains(text(), 'New Client Engagement')]")).Count == 0);
        var modals = Driver.FindElements(By.XPath("//h2[contains(text(), 'New Client Engagement')]"));
        Assert.Empty(modals);
    }
}
