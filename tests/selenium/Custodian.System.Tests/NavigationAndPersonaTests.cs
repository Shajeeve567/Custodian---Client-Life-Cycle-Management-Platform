using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace Custodian.System.Tests;

public class NavigationAndPersonaTests : TestBase
{
    [Fact]
    public void TC01_BrandHeader_ShouldDisplay_CustodianPlatformTitle()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        var title = Wait.Until(d => d.FindElement(By.ClassName("brand-title")));
        var subtitle = Driver.FindElement(By.ClassName("brand-subtitle"));

        Assert.Equal("Custodian", title.Text);
        Assert.Equal("B2B Client Lifecycle Platform", subtitle.Text);
    }

    [Fact]
    public void TC02_Tabs_ShouldSwitchActiveState_OnClick()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Click Document Vault tab
        var docTab = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Document Vault')]")));
        docTab.Click();
        Assert.Contains("active", docTab.GetAttribute("class"));

        // Click Genesis Audit Log tab
        var auditTab = Driver.FindElement(By.XPath("//button[contains(., 'Genesis Audit Log')]"));
        auditTab.Click();
        Assert.Contains("active", auditTab.GetAttribute("class"));
    }

    [Fact]
    public void TC03_PersonaSwitch_ToClient_ShouldUpdateActionTabLabel()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Switch Persona dropdown to 'Client'
        var roleDropdown = Wait.Until(d => d.FindElement(By.ClassName("role-select")));
        var selectRole = new SelectElement(roleDropdown);
        selectRole.SelectByValue("Client");

        // The second tab should dynamically re-label from 'Staff Actions' to 'Client Portal'
        var clientPortalTab = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Client Portal')]")));
        Assert.True(clientPortalTab.Displayed);
    }

    [Fact]
    public void TC04_TenantDropdown_ShouldAllowSelectingDifferentTenants()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        var tenantDropdown = Wait.Until(d => d.FindElement(By.XPath("//label[text()='Tenant:']/following-sibling::select")));
        var selectTenant = new SelectElement(tenantDropdown);

        selectTenant.SelectByValue("tenant-beta");
        Assert.Equal("tenant-beta", selectTenant.SelectedOption.GetAttribute("value"));

        selectTenant.SelectByValue("qa-environment");
        Assert.Equal("qa-environment", selectTenant.SelectedOption.GetAttribute("value"));
    }
}

