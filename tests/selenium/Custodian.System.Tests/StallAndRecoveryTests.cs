using System.Linq;
using System.Threading.Tasks;
using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class StallAndRecoveryTests : TestBase
{
    [Fact]
    public async Task TC08C_StallAndIntervention_OverdueStall_RecordIntervention_Success()
    {
        // 1. Prepare workspace, client, engagement, and checklist
        var ws = await QaApiHelper.RegisterAndSetupWorkspaceAsync("S3-Stall");
        var client = await QaApiHelper.CreateClientWithPortalUserAsync(ws.Token, ws.TenantId, "S3-Stall");
        var engId = await QaApiHelper.CreateEngagementAsync(ws.Token, ws.TenantId, client.ClientId, ws.UserId);
        await QaApiHelper.StartEngagementAsync(ws.Token, ws.TenantId, engId);
        await QaApiHelper.ApplyStandardChecklistAsync(ws.Token, ws.TenantId, engId);

        // 2. Make a Stage 1 pending client action overdue (triggers deterministic stall)
        var actions = await QaApiHelper.GetActionsAsync(ws.Token, ws.TenantId, engId);
        var clientAction = actions.First(a => a.StageNumber == 1 && a.AssignedToRole == "Client" && !a.IsInternalOnly);
        await QaApiHelper.MakeActionOverdueAsync(ws.Token, ws.TenantId, engId, clientAction.ActionId, daysAgo: 3);

        // 3. Open Stall Queue as Owner
        InjectRealAuthSession(ws.Token, ws.TenantId, ws.TenantName);
        Driver.Navigate().GoToUrl($"{BaseUrl}/stall-queue");

        // Verify stalled engagement appears in Staff Stall Queue
        var stallRow = Wait.Until(d => d.FindElement(By.XPath($"//tr[contains(., '{client.Name}') or contains(., '{engId.Substring(0, 6).ToUpper()}')]")));
        Assert.True(stallRow.Displayed);
        CaptureEvidence("06-stall-queue.png");

        // 4. Click 'Record Intervention'
        var recordBtn = stallRow.FindElement(By.XPath(".//button[contains(text(), 'Record Intervention')]"));
        recordBtn.Click();

        // 5. Verify Intervention Modal opens
        var modalHeader = Wait.Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'Record Intervention')]")));
        Assert.True(modalHeader.Displayed);

        // Fill valid reason and submit
        var reasonTextarea = Driver.FindElement(By.XPath("//textarea"));
        reasonTextarea.Clear();
        reasonTextarea.SendKeys("Client contacted via phone; agreed to submit required identity documentation within 24 hours.");

        var submitInterventionBtn = Driver.FindElement(By.XPath("//div[contains(@class, 'fixed')]//button[@type='submit' and contains(., 'Record Intervention')]"));
        submitInterventionBtn.Click();

        // Verify clear success state shown
        var successNotice = Wait.Until(d => d.FindElement(By.XPath("//span[contains(normalize-space(.), 'Intervention recorded')]")));
        Assert.True(successNotice.Displayed);
        CaptureEvidence("07-intervention-modal-or-success.png");

        // 6. Complete the blocker action to recover from stall
        await QaApiHelper.CompleteActionAsync(ws.Token, ws.TenantId, engId, clientAction.ActionId);

        // 7. Navigate to Workspace and verify Next Action recalculates without stall badge
        Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{engId}");
        Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Next Action')]")));

        // Verify Next Action does NOT show the 'Stalled' badge
        var stalledBadges = Driver.FindElements(By.XPath("//section[@aria-label='Next action']//span[contains(text(), 'Stalled')]"));
        Assert.Empty(stalledBadges);

        CaptureEvidence("08-recovery-next-action.png");
    }
}
