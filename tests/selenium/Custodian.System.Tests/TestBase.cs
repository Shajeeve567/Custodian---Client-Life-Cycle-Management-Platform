using System;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace Custodian.System.Tests;

public abstract class TestBase : IDisposable
{
    protected readonly IWebDriver Driver;
    protected readonly WebDriverWait Wait;
    protected const string BaseUrl = "http://localhost:3000";

    protected TestBase()
    {
        var options = new ChromeOptions();
        
        // Use headless mode if CI or SELENIUM_HEADLESS environment variable is set
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELENIUM_HEADLESS")))
        {
            options.AddArgument("--headless=new");
        }
        
        options.AddArgument("--window-size=1440,900");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Injects an authenticated mock session into localStorage.
    /// This bypasses database writes for Sprint 1 UI testing while exercising protected dashboards.
    /// </summary>
    protected void InjectMockAuthSession(string role = "Owner", string tenantId = "tenant-alpha", string tenantName = "Alpha Corp")
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Valid base64 encoded JWT with sub, email, role, tenant_id and far-future expiration (2100)
        string mockToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ1c3ItdGVzdC0wMDEiLCJlbWFpbCI6Im93bmVyMUB0ZXN0LmNvbSIsInJvbGUiOiJPd25lciIsInRlbmFudF9pZCI6InRlbmFudC1hbHBoYSIsImV4cCI6NDEwMjQ0NDgwMH0.signature";

        var js = (IJavaScriptExecutor)Driver;
        js.ExecuteScript(
            $"localStorage.setItem('custodian_token', '{mockToken}');" +
            $"localStorage.setItem('custodian_tenant_id', '{tenantId}');" +
            $"localStorage.setItem('custodian_tenant_name', '{tenantName}');"
        );
    }

    public void Dispose()
    {
        try
        {
            Driver.Quit();
            Driver.Dispose();
        }
        catch
        {
            // Teardown safety
        }
    }
}
