using System.Threading.Tasks;
using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class AccessBoundaryTests : TestBase
{
    [Fact]
    public async Task TC13_PR48_RoleAndStaffAccessBoundaries()
    {
        // 1. Setup workspace with an engagement and client
        var ws = await QaApiHelper.RegisterAndSetupWorkspaceAsync("S3-Sec");
        var client = await QaApiHelper.CreateClientWithPortalUserAsync(ws.Token, ws.TenantId, "S3-Sec");
        var engId = await QaApiHelper.CreateEngagementAsync(ws.Token, ws.TenantId, client.ClientId, ws.UserId);

        // 2. Owner can access the engagement workspace
        InjectRealAuthSession(ws.Token, ws.TenantId, ws.TenantName);
        Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engId}");
        Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Next Action')] | //span[contains(text(), 'Draft')]")));
        Assert.Contains($"/workspace/{engId}", Driver.Url);

        // 3. Client login: Attempt direct navigation to staff-only routes
        // Explicitly clear Owner authentication session from localStorage before role switch
        var js = (IJavaScriptExecutor)Driver;
        js.ExecuteScript(
            "localStorage.removeItem('custodian_token');" +
            "localStorage.removeItem('custodian_tenant_id');" +
            "localStorage.removeItem('custodian_tenant_name');"
        );

        // Client logs in
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        var emailInput = Wait.Until(d => d.FindElement(By.XPath("//input[@type='email']")));
        emailInput.Clear();
        emailInput.SendKeys(client.Email);

        var passwordInput = Driver.FindElement(By.XPath("//input[@type='password']"));
        passwordInput.Clear();
        passwordInput.SendKeys(client.Password);

        var submitBtn = Driver.FindElement(By.XPath("//button[@type='submit']"));
        submitBtn.Click();

        Wait.Until(d => d.Url.Contains("/portal"));

        // Client attempts to navigate directly to Staff Stall Queue
        Driver.Navigate().GoToUrl($"{BaseUrl}/stall-queue");

        // ProtectedRoute role guard redirects Client back to /portal
        Wait.Until(d => d.Url.Contains("/portal"));
        Assert.DoesNotContain("/stall-queue", Driver.Url);

        // Client attempts to navigate directly to Reports
        Driver.Navigate().GoToUrl($"{BaseUrl}/reports");

        // ProtectedRoute role guard redirects Client back to /portal
        Wait.Until(d => d.Url.Contains("/portal"));
        Assert.DoesNotContain("/reports", Driver.Url);

        CaptureEvidence("12-access-boundary.png");
    }
}
