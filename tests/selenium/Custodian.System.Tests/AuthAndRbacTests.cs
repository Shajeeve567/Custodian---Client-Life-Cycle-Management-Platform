using System;
using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

/// <summary>
/// Streamlined Workflow 1: Authentication, Collision Rejection, and Role-Based Access Control (RBAC).
/// Validates Identity microservice auth boundaries, JWT issuance, duplicate account protection,
/// and client route guards in a fast, cohesive suite.
/// </summary>
public class AuthAndRbacTests : TestBase
{
    [Fact]
    [Trait("Category", "CI")]
    public void AUTH01_RegistrationDuplicateRejectionAndProtectedRoutes()
    {
        try
        {
            // 1. Unauthenticated route protection: attempting to visit /engagements redirects to /login
            Driver.Navigate().GoToUrl($"{BaseUrl}/engagements");
            Wait.Until(d => d.Url.Contains("/login"));
            Assert.Contains("/login", Driver.Url);
            TestEnvironment.DemoPause(300);

            // 2. Invalid credentials rejection
            var emailInput = Wait.Until(d => d.FindElement(By.CssSelector("input[type='email']")));
            emailInput.Clear();
            emailInput.SendKeys("invalid.qa.tester@example.com");

            var passwordInput = Driver.FindElement(By.CssSelector("input[type='password']"));
            passwordInput.Clear();
            passwordInput.SendKeys("IncorrectP@ssw0rd999!");

            var submitBtn = Driver.FindElement(By.CssSelector("button.auth-submit-btn"));
            submitBtn.Click();
            TestEnvironment.DemoPause(400);

            var errorBanner = Wait.Until(d => d.FindElement(By.ClassName("auth-error-banner")));
            Assert.True(errorBanner.Displayed);
            Assert.Contains("Invalid email or password", errorBanner.Text);
            Assert.Contains("/login", Driver.Url);

            // 3. Collision-resistant new user registration
            Driver.Navigate().GoToUrl($"{BaseUrl}/register");
            Wait.Until(d => d.Url.Contains("/register"));

            string uniqueId = Guid.NewGuid().ToString("N")[..8];
            string testEmail = $"se3112.qa.reg.{uniqueId}@example.com";
            string testPassword = "P@ssw0rd123!";

            emailInput = Wait.Until(d => d.FindElement(By.CssSelector("input[type='email']")));
            emailInput.Clear();
            emailInput.SendKeys(testEmail);

            passwordInput = Driver.FindElement(By.CssSelector("input[type='password']"));
            passwordInput.Clear();
            passwordInput.SendKeys(testPassword);
            TestEnvironment.DemoPause(300);

            submitBtn = Driver.FindElement(By.CssSelector("button.auth-submit-btn"));
            submitBtn.Click();

            // Wait for backend registration and global token acquisition; lands on workspace resolution hub
            Wait.Until(d =>
            {
                var banners = d.FindElements(By.ClassName("auth-error-banner"));
                if (banners.Count > 0 && banners[0].Displayed)
                {
                    throw new InvalidOperationException($"Registration failed: {banners[0].Text}");
                }
                var nodeSpans = d.FindElements(By.XPath("//span[contains(text(), 'WORKSPACE RESOLUTION NODE')]"));
                var agencyHeaders = d.FindElements(By.XPath("//h3[contains(., 'Initialize Agency Workspace')]"));
                return nodeSpans.Count > 0 && agencyHeaders.Count > 0;
            });

            var setupNode = Driver.FindElement(By.XPath("//span[contains(text(), 'WORKSPACE RESOLUTION NODE')]"));
            Assert.True(setupNode.Displayed);
            TestEnvironment.DemoPause(400);

            // 4. Duplicate registration rejection: attempt registering with same email again in fresh session
            SwitchToFreshIndependentBrowserSession();
            Driver.Navigate().GoToUrl($"{BaseUrl}/register");
            Wait.Until(d => d.Url.Contains("/register"));

            emailInput = Wait.Until(d => d.FindElement(By.CssSelector("input[type='email']")));
            emailInput.Clear();
            emailInput.SendKeys(testEmail);

            passwordInput = Driver.FindElement(By.CssSelector("input[type='password']"));
            passwordInput.Clear();
            passwordInput.SendKeys(testPassword);
            TestEnvironment.DemoPause(300);

            submitBtn = Driver.FindElement(By.CssSelector("button.auth-submit-btn"));
            submitBtn.Click();

            var dupErrorBanner = Wait.Until(d =>
            {
                var banners = d.FindElements(By.ClassName("auth-error-banner"));
                return banners.Count > 0 && banners[0].Displayed ? banners[0] : null;
            });

            Assert.NotNull(dupErrorBanner);
            Assert.Contains("Email already exists", dupErrorBanner.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("/register", Driver.Url);
        }
        catch (Exception)
        {
            CaptureScreenshot("AUTH01_Failure");
            throw;
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public void AUTH02_RoleBasedAccessAndRouteGuardMatrix()
    {
        try
        {
            // =========================================================================
            // ROLE 1: OWNER (Workspace access + exclusive Team Management controls)
            // =========================================================================
            LoginViaUi(TestEnvironment.OwnerEmail, TestEnvironment.OwnerPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/engagements"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);

            var manageTeamBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Manage Team')]")));
            Assert.True(manageTeamBtn.Displayed);
            TestEnvironment.DemoPause(300);

            // =========================================================================
            // ROLE 2: STAFF (Assigned engagements access + Team Management forbidden)
            // =========================================================================
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/engagements"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);

            var engagementCards = Driver.FindElements(By.XPath("//div[contains(@class, 'group') and .//div[contains(text(), 'ET') or contains(text(), 'E2E Test Client')]]"));
            Assert.NotEmpty(engagementCards);

            // Verify Manage Team control is strictly absent for Staff
            var staffManageTeamButtons = Driver.FindElements(By.XPath("//button[contains(., 'Manage Team')]"));
            Assert.Empty(staffManageTeamButtons);
            TestEnvironment.DemoPause(300);

            // =========================================================================
            // ROLE 3: CLIENT (Auto /portal redirect + route guard bounces /engagements)
            // =========================================================================
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.ClientEmail, TestEnvironment.ClientPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/portal"));
            Assert.Contains("/portal", Driver.Url);

            var clientPill = Wait.Until(d => d.FindElement(By.XPath("//header//span[contains(@title, 'Client Portal')]")));
            Assert.True(clientPill.Displayed);

            // Verify staff nav links are absent
            Assert.Empty(Driver.FindElements(By.XPath("//nav[@aria-label='Main']//a[contains(text(), 'Engagements')]")));

            // Safe Read-Only RBAC Route Guard: attempt navigating to Owner/Staff route (/engagements)
            Driver.Navigate().GoToUrl($"{BaseUrl}/engagements");
            Wait.Until(d => d.Url.Contains("/portal"));
            Assert.Contains("/portal", Driver.Url);
            TestEnvironment.DemoPause(300);
        }
        catch (Exception)
        {
            CaptureScreenshot("AUTH02_Failure");
            throw;
        }
    }
}
