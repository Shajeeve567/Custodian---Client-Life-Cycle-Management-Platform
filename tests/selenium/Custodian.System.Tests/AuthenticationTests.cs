using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class AuthenticationTests : TestBase
{
    [Fact]
    public void TC03_AuthPage_ToggleBetweenLoginAndRegisterModes()
    {
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");

        // Verify initial state has Sign In active
        var signInTab = Wait.Until(d => d.FindElement(By.XPath("//button[contains(@class, 'segmented-tab') and contains(text(), 'Sign In')]")));
        Assert.Contains("segmented-tab-active", signInTab.GetAttribute("class"));

        // Click Create Account tab
        var createAccountTab = Driver.FindElement(By.XPath("//button[contains(@class, 'segmented-tab') and contains(text(), 'Create Account')]"));
        createAccountTab.Click();

        // Verify URL updates to /register and tab becomes active
        Wait.Until(d => d.Url.Contains("/register"));
        Assert.Contains("/register", Driver.Url);
        Assert.Contains("segmented-tab-active", createAccountTab.GetAttribute("class"));

        // Switch back to Sign In
        signInTab = Driver.FindElement(By.XPath("//button[contains(@class, 'segmented-tab') and contains(text(), 'Sign In')]"));
        signInTab.Click();

        Wait.Until(d => d.Url.Contains("/login"));
        Assert.Contains("/login", Driver.Url);
        Assert.Contains("segmented-tab-active", signInTab.GetAttribute("class"));
    }

    [Fact]
    public void TC04_AuthPage_PasswordVisibilityToggle()
    {
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");

        var passwordInput = Wait.Until(d => d.FindElement(By.XPath("//input[@type='password']")));
        passwordInput.Clear();
        passwordInput.SendKeys("CustodianPass2026!");

        Assert.Equal("password", passwordInput.GetAttribute("type"));

        // Click show password toggle button
        var toggleBtn = Driver.FindElement(By.ClassName("auth-input-action"));
        toggleBtn.Click();

        // Password input type should toggle to 'text'
        Assert.Equal("text", passwordInput.GetAttribute("type"));

        // Click again to hide password
        toggleBtn.Click();
        Assert.Equal("password", passwordInput.GetAttribute("type"));
    }

    [Fact]
    public void TC05_ProtectedRoute_ShouldRedirectUnauthenticatedUser()
    {
        // Try accessing protected dashboard route directly without logging in
        Driver.Navigate().GoToUrl($"{BaseUrl}/engagements");

        // ProtectedRoute should intercept and navigate to /login
        Wait.Until(d => d.Url.Contains("/login"));
        Assert.Contains("/login", Driver.Url);

        // Verify auth form is displayed
        var authForm = Wait.Until(d => d.FindElement(By.ClassName("auth-form")));
        Assert.True(authForm.Displayed);
    }
}
