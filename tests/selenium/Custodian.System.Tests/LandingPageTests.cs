using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class LandingPageTests : TestBase
{
    [Fact]
    public void TC01_LandingPage_ShouldDisplay_CustodianBrandingAndKeySections()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Verify Brand Title
        var brandTitle = Wait.Until(d => d.FindElement(By.ClassName("landing-brand-title")));
        Assert.Equal("Custodian", brandTitle.Text.Trim());

        // Verify Hero Headline
        var heroTitle = Wait.Until(d => d.FindElement(By.ClassName("landing-hero-title")));
        Assert.Contains("Client onboarding without the chaos", heroTitle.Text);

        // Verify Public Nav Anchor Links
        var navLinks = Driver.FindElement(By.ClassName("landing-nav-links"));
        Assert.True(navLinks.Displayed);
        Assert.NotNull(Driver.FindElement(By.XPath("//nav[contains(@class, 'landing-nav-links')]//a[@href='#problem']")));
        Assert.NotNull(Driver.FindElement(By.XPath("//nav[contains(@class, 'landing-nav-links')]//a[@href='#engine']")));
        Assert.NotNull(Driver.FindElement(By.XPath("//nav[contains(@class, 'landing-nav-links')]//a[@href='#security']")));

        // Verify Call-to-Action buttons
        var ctaGroup = Driver.FindElement(By.ClassName("landing-cta-group"));
        Assert.True(ctaGroup.Displayed);
        var signInBtn = ctaGroup.FindElement(By.XPath(".//a[contains(text(), 'Sign In')]"));
        var registerBtn = ctaGroup.FindElement(By.XPath(".//a[contains(., 'Initialize Workspace')]"));
        Assert.True(signInBtn.Displayed);
        Assert.True(registerBtn.Displayed);
    }

    [Fact]
    public void TC02_LandingPage_NavigationToAuth()
    {
        Driver.Navigate().GoToUrl(BaseUrl);

        // Click the 'Sign In' CTA
        var signInBtn = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'landing-cta-group')]//a[contains(text(), 'Sign In')]")));
        signInBtn.Click();

        // Wait until redirected to /login
        Wait.Until(d => d.Url.Contains("/login"));
        Assert.Contains("/login", Driver.Url);

        // Verify the Auth card mode switcher rendered
        var switcher = Wait.Until(d => d.FindElement(By.ClassName("segmented-switcher")));
        Assert.True(switcher.Displayed);
    }
}
