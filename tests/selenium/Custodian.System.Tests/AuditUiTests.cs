using System.Threading.Tasks;
using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class AuditUiTests : TestBase
{
    [Fact]
    public async Task TC08E_AuditAndAccess_AuditLog_ChainVerification_StaffIsolation()
    {
        // 1. Prepare workspace, client, engagement, and staff accounts
        var ws = await QaApiHelper.RegisterAndSetupWorkspaceAsync("S3-Audit");
        var client = await QaApiHelper.CreateClientWithPortalUserAsync(ws.Token, ws.TenantId, "S3-Audit");
        var assignedStaff = await QaApiHelper.CreateStaffUserAndLoginAsync(ws.Token, ws.TenantId, ws.TenantName, "Assigned");
        var unassignedStaff = await QaApiHelper.CreateStaffUserAndLoginAsync(ws.Token, ws.TenantId, ws.TenantName, "Unassigned");

        var engId = await QaApiHelper.CreateEngagementAsync(ws.Token, ws.TenantId, client.ClientId, assignedStaff.UserId);
        await QaApiHelper.StartEngagementAsync(ws.Token, ws.TenantId, engId);

        // 2. Open /audit as Owner
        InjectRealAuthSession(ws.Token, ws.TenantId, ws.TenantName);
        Driver.Navigate().GoToUrl($"{BaseUrl}/audit");

        // Verify Audit Log Page Title & KPI cards render
        var title = Wait.Until(d => d.FindElement(By.XPath("//h1[contains(text(), 'Audit Log')]")));
        Assert.True(title.Displayed);

        var eventsKpi = Wait.Until(d => d.FindElement(By.XPath("//div[contains(., 'Events') and contains(@class, 'p-5')]")));
        Assert.True(eventsKpi.Displayed);
        CaptureEvidence("11-audit-log.png");

        // 3. Trigger Hash Chain Verification through UI
        var verifyBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Verify all chains') or contains(., 'Verify this engagement') or contains(., 'Verify')]")));
        Assert.True(verifyBtn.Displayed);
        verifyBtn.Click();

        // Verify Cryptographic Integrity state in UI
        var verifiedBadge = Wait.Until(d => d.FindElement(By.XPath("//div[contains(., 'Hash chains')]//div[contains(@class, 'bg-emerald-50') or contains(., 'intact') or contains(., 'verified') or contains(., 'Valid')] | //span[contains(text(), 'Verified') or contains(text(), 'Intact')]")));
        Assert.True(verifiedBadge.Displayed);

        // 4. Assigned Staff access: can open and see the assigned engagement workspace
        InjectRealAuthSession(assignedStaff.Token, ws.TenantId, ws.TenantName);
        Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engId}");
        var nextActionOrStage = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Next Action')] | //span[contains(text(), 'Started')]")));
        Assert.True(nextActionOrStage.Displayed);

        // 5. Unassigned Staff access: cannot see engagement on dashboard and blocked from workspace
        InjectRealAuthSession(unassignedStaff.Token, ws.TenantId, ws.TenantName);
        Driver.Navigate().GoToUrl($"{BaseUrl}/engagements");
        Wait.Until(d => d.FindElement(By.XPath("//h1[contains(text(), 'Client Engagements')] | //table | //div[contains(@class, 'space-y-4')]")));
        var unassignedRow = Driver.FindElements(By.XPath($"//tr[contains(., '{engId.Substring(0, 8)}')]"));
        Assert.Empty(unassignedRow);

        // Direct navigation by unassigned staff is blocked/denied: Next Action and engagement data are concealed (404 / error banner)
        Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engId}");
        var accessConcealedNotice = Wait.Until(d => d.FindElement(By.XPath(
            "//span[contains(normalize-space(.), 'Could not load the next action') or contains(normalize-space(.), 'Engagement not found')] | " +
            "//div[contains(@class, 'bg-rose-50') and (contains(., 'not found') or contains(., 'unavailable'))] | " +
            "//h1[contains(text(), 'Client Engagements')]"
        )));
        Assert.True(accessConcealedNotice.Displayed);

        // Verify protected engagement data / active next-action state is not accessible to unassigned staff
        var activeStateChips = Driver.FindElements(By.XPath("//section[@aria-label='Next action']//span[contains(text(), 'Client action required') or contains(text(), 'Awaiting staff') or contains(text(), 'Ready to advance')]"));
        Assert.Empty(activeStateChips);
        CaptureEvidence("12-access-boundary.png");
    }
}
