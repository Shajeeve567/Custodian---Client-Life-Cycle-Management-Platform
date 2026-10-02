using System;
using System.IO;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace Custodian.System.Tests;

public abstract class TestBase : IDisposable
{
    protected readonly IWebDriver Driver;
    protected readonly WebDriverWait Wait;
    protected const string BaseUrl = "http://localhost:3000";
    protected readonly string DownloadDir;
    protected static readonly string EvidenceDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../evidence/sprint3-final")
    );

    protected TestBase()
    {
        DownloadDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "downloads", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(DownloadDir);
        Directory.CreateDirectory(EvidenceDir);

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

        // Enable automatic downloads
        options.AddUserProfilePreference("download.default_directory", DownloadDir);
        options.AddUserProfilePreference("download.prompt_for_download", false);
        options.AddUserProfilePreference("download.directory_upgrade", true);
        options.AddUserProfilePreference("safebrowsing.enabled", true);

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(15));
    }

    /// <summary>
    /// Captures a screenshot to tests/selenium/evidence/sprint3-final/
    /// </summary>
    public void CaptureEvidence(string filename)
    {
        try
        {
            Directory.CreateDirectory(EvidenceDir);
            var screenshot = ((ITakesScreenshot)Driver).GetScreenshot();
            var targetPath = Path.Combine(EvidenceDir, filename);
            screenshot.SaveAsFile(targetPath);
            Console.WriteLine($"📸 QA REPORT EVIDENCE: {targetPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to capture evidence {filename}: {ex.Message}");
        }
    }

    /// <summary>
    /// Injects an authenticated real session into localStorage.
    /// </summary>
    protected void InjectRealAuthSession(string token, string tenantId, string tenantName)
    {
        Driver.Navigate().GoToUrl(BaseUrl);
        var js = (IJavaScriptExecutor)Driver;
        js.ExecuteScript(
            $"localStorage.setItem('custodian_token', '{token}');" +
            $"localStorage.setItem('custodian_tenant_id', '{tenantId}');" +
            $"localStorage.setItem('custodian_tenant_name', '{tenantName}');"
        );
    }

    /// <summary>
    /// Injects an authenticated mock session into localStorage.
    /// Backward compatible for legacy Sprint 1 tests.
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

        try
        {
            if (Directory.Exists(DownloadDir))
            {
                Directory.Delete(DownloadDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
