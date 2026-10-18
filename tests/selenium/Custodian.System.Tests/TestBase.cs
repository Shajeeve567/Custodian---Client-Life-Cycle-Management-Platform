using System;
using System.IO;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace Custodian.System.Tests;

public abstract class TestBase : IDisposable
{
    protected IWebDriver Driver { get; set; }
    protected WebDriverWait Wait { get; set; }
    protected static string BaseUrl => TestEnvironment.BaseUrl;

    static TestBase()
    {
        TestEnvironment.Initialize();
    }

    protected TestBase()
    {
        Driver = CreateDriver();
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(25));
    }

    /// <summary>
    /// Instantiates a new ChromeDriver instance configured with current demonstration and headless settings.
    /// </summary>
    protected IWebDriver CreateDriver()
    {
        var options = new ChromeOptions();

        // Demonstration mode: visible browser window with deliberate pacing.
        // Headless mode: enabled in CI or when SELENIUM_HEADLESS=true (unless Demo Mode is requested).
        bool isDemo = TestEnvironment.IsDemoMode;
        bool isHeadlessExplicit = string.Equals(Environment.GetEnvironmentVariable("SELENIUM_HEADLESS"), "true", StringComparison.OrdinalIgnoreCase);
        bool isCi = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

        if (!isDemo && (isHeadlessExplicit || isCi))
        {
            options.AddArgument("--headless=new");
        }

        options.AddArgument("--window-size=1440,900");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");

        var driver = new ChromeDriver(options);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
        return driver;
    }

    /// <summary>
    /// Safely terminates the current browser session and launches a fresh, independent
    /// browser session with a dedicated WebDriver instance and empty cookie/cache state.
    /// Prevents parallel session collisions and eliminates cookie/token bleed across roles.
    /// </summary>
    protected void SwitchToFreshIndependentBrowserSession()
    {
        try
        {
            Driver?.Quit();
            Driver?.Dispose();
        }
        catch
        {
            // Session cleanup safety
        }

        Driver = CreateDriver();
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(25));
    }

    /// <summary>
    /// Safely terminates the current browser session and launches a fresh browser session
    /// configured with automatic downloads directed to the specified local directory.
    /// Eliminates OS download prompts for seamless automated file assertion.
    /// </summary>
    protected void SwitchToDownloadEnabledBrowserSession(string downloadDirectory)
    {
        try
        {
            Driver?.Quit();
            Driver?.Dispose();
        }
        catch
        {
            // Session cleanup safety
        }

        Directory.CreateDirectory(downloadDirectory);
        var options = new ChromeOptions();

        bool isDemo = TestEnvironment.IsDemoMode;
        bool isHeadlessExplicit = string.Equals(Environment.GetEnvironmentVariable("SELENIUM_HEADLESS"), "true", StringComparison.OrdinalIgnoreCase);
        bool isCi = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));

        if (!isDemo && (isHeadlessExplicit || isCi))
        {
            options.AddArgument("--headless=new");
        }

        options.AddArgument("--window-size=1440,900");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");

        options.AddUserProfilePreference("download.default_directory", downloadDirectory);
        options.AddUserProfilePreference("download.prompt_for_download", false);
        options.AddUserProfilePreference("download.directory_upgrade", true);
        options.AddUserProfilePreference("plugins.always_open_pdf_externally", true);

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
        Wait = new WebDriverWait(Driver, TimeSpan.FromSeconds(25));
    }

    /// <summary>
    /// Performs genuine UI-based authentication and workspace resolution through the real React frontend.
    /// Handles both single-workspace auto-selection and multi-workspace hub selection.
    /// </summary>
    protected void LoginViaUi(string email, string password, string? expectedTenantName = null)
    {
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        TestEnvironment.DemoPause(500);

        // 1. Fill email
        var emailInput = Wait.Until(d => d.FindElement(By.CssSelector("input[type='email']")));
        emailInput.Clear();
        emailInput.SendKeys(email);
        TestEnvironment.DemoPause(300);

        // 2. Fill password
        var passwordInput = Driver.FindElement(By.CssSelector("input[type='password']"));
        passwordInput.Clear();
        passwordInput.SendKeys(password);
        TestEnvironment.DemoPause(300);

        // 3. Click submit button
        var submitBtn = Driver.FindElement(By.CssSelector("button.auth-submit-btn"));
        submitBtn.Click();
        TestEnvironment.DemoPause(600);

        // 4. Handle resolution: either auto-navigated to target dashboard or workspace selection hub appeared
        Wait.Until(d =>
        {
            // Successfully transitioned away from /login
            if (!d.Url.Contains("/login")) return true;

            // Check if intermediate workspace hub is visible
            var workspaceCards = d.FindElements(By.XPath("//button[contains(@class, 'group') and .//div[contains(@class, 'bg-gradient-to-br')]]"));
            if (workspaceCards.Count > 0)
            {
                var targetTenant = expectedTenantName ?? TestEnvironment.TenantName;
                foreach (var card in workspaceCards)
                {
                    if (card.Text.Contains(targetTenant, StringComparison.OrdinalIgnoreCase))
                    {
                        TestEnvironment.DemoPause(500);
                        card.Click();
                        return true;
                    }
                }
                // Fallback to first available workspace
                workspaceCards[0].Click();
                return true;
            }

            return false;
        });

        // 5. Confirm navigation away from /login completed
        Wait.Until(d => !d.Url.Contains("/login"));
        TestEnvironment.DemoPause(500);
    }

    /// <summary>
    /// Performs UI-based sign out through the header account menu.
    /// </summary>
    protected void LogoutViaUi()
    {
        try
        {
            var accountBtn = Wait.Until(d => d.FindElement(By.CssSelector("button[aria-label='Account menu']")));
            accountBtn.Click();
            TestEnvironment.DemoPause(300);

            var signOutBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Sign out')]")));
            signOutBtn.Click();
            Wait.Until(d => d.Url.Contains("/login") || d.Url == BaseUrl + "/");
            TestEnvironment.DemoPause(500);
        }
        catch
        {
            // Fallback: clear local storage session and redirect
            ((IJavaScriptExecutor)Driver).ExecuteScript("localStorage.clear();");
            Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        }
    }

    /// <summary>
    /// Captures a diagnostic screenshot on test failure into the bin/Screenshots output folder.
    /// </summary>
    protected string? CaptureScreenshot(string testName)
    {
        try
        {
            var screenshotDriver = Driver as ITakesScreenshot;
            if (screenshotDriver == null) return null;

            var screenshot = screenshotDriver.GetScreenshot();
            var dir = Path.Combine(AppContext.BaseDirectory, "Screenshots");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var filePath = Path.Combine(dir, $"{testName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
            screenshot.SaveAsFile(filePath);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Injects an authenticated mock session into localStorage.
    /// Kept for backward compatibility with initial Sprint 1 prototype tests.
    /// Real E2E scenarios must use LoginViaUi() instead.
    /// </summary>
    [Obsolete("Use LoginViaUi() with genuine Identity credentials for true E2E tests.")]
    protected void InjectMockAuthSession(string role = "Owner", string tenantId = "tenant-alpha", string tenantName = "Alpha Corp")
    {
        Driver.Navigate().GoToUrl(BaseUrl);

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
            if (TestEnvironment.IsDemoMode)
            {
                TestEnvironment.DemoPause(1000);
            }
            Driver?.Quit();
            Driver?.Dispose();
        }
        catch
        {
            // Teardown safety
        }
    }
}
