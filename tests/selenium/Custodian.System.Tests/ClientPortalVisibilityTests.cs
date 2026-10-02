using System.Threading.Tasks;
using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class ClientPortalVisibilityTests : TestBase
{
    [Fact]
    public async Task TC08B_ClientVisibility_ActiveActionsVisible_InternalNotesHidden()
    {
        // 1. Prepare workspace, client with portal credentials, engagement and checklist
        var ws = await QaApiHelper.RegisterAndSetupWorkspaceAsync("S3-Portal");
        var client = await QaApiHelper.CreateClientWithPortalUserAsync(ws.Token, ws.TenantId, "S3-Portal");
        var engId = await QaApiHelper.CreateEngagementAsync(ws.Token, ws.TenantId, client.ClientId, ws.UserId);
        await QaApiHelper.StartEngagementAsync(ws.Token, ws.TenantId, engId);
        await QaApiHelper.ApplyStandardChecklistAsync(ws.Token, ws.TenantId, engId);

        // 2. Client logs in through the browser
        Driver.Navigate().GoToUrl($"{BaseUrl}/login");
        var emailInput = Wait.Until(d => d.FindElement(By.XPath("//input[@type='email']")));
        emailInput.Clear();
        emailInput.SendKeys(client.Email);

        var passwordInput = Driver.FindElement(By.XPath("//input[@type='password']"));
        passwordInput.Clear();
        passwordInput.SendKeys(client.Password);

        var submitBtn = Driver.FindElement(By.XPath("//button[@type='submit']"));
        submitBtn.Click();

        // 3. Client lands on /portal
        Wait.Until(d => d.Url.Contains("/portal"));

        // 4. Verify client portal branding and client action visibility
        var portalHeader = Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Client Access') or contains(text(), 'Client Portal')] | //h1[contains(., 'Client Portal')]")));
        Assert.True(portalHeader.Displayed);

        // Verify active client actions are displayed
        var actionCard = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'rounded') and (contains(., 'Upload') or contains(., 'Document') or contains(., 'Agreement') or contains(., 'Information'))]")));
        Assert.True(actionCard.Displayed);

        CaptureEvidence("05-client-portal-action.png");

        // 5. Verify internal/staff-only action information is NOT exposed to client
        var internalStaffBadges = Driver.FindElements(By.XPath("//*[contains(text(), 'Internal Staff Note') or contains(text(), 'Staff Only') or contains(text(), 'Staff Review')]"));
        Assert.Empty(internalStaffBadges);
    }
}
