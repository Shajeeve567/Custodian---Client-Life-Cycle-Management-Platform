using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace Custodian.System.Tests;

/// <summary>
/// Streamlined Workflow 4: Document Compliance, File Guards, Rejection & Verification Lifecycle.
/// Validates Documents microservice compliance policy guards (PDF only, 5MB limit, required dates),
/// and the multi-role review workflow (Staff rejection with reason -> Client revision feedback -> Resubmission -> Staff verification).
/// </summary>
public class DocumentComplianceTests : TestBase
{
    private readonly List<string> _tempFiles = new();

    private const string Stage2EngagementId = "4ca380d3-04e0-4be5-8e6a-6f8a760803b6";

    [Fact]
    [Trait("Category", "CI")]
    public void DOC01_Client_UploadValidationGuards_FileTypesAndSizeLimits()
    {
        try
        {
            // 1. Authenticate as QA Client into Client Portal
            LoginViaUi(TestEnvironment.Stage2ClientEmail, TestEnvironment.Stage2ClientPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/portal"));
            Wait.Until(d => d.FindElements(By.XPath("//h1[contains(., 'Client Onboarding Portal')] | //h3[contains(., 'Compliance Document Vault')]")).Count > 0);
            TestEnvironment.DemoPause(400);

            // 2. Open evidence upload modal
            var uploadTriggerBtn = Wait.Until(d =>
            {
                var elements = d.FindElements(By.XPath("//button[contains(., 'Upload New Document') or contains(., 'Upload Additional Evidence') or contains(., 'Upload Evidence') or contains(., 'Upload')] | //span[contains(text(), 'Click here to upload evidence')]"));
                return elements.FirstOrDefault(b => b.Displayed && b.Enabled);
            });
            Assert.NotNull(uploadTriggerBtn);
            uploadTriggerBtn.Click();
            TestEnvironment.DemoPause(300);

            var modal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Upload Evidence Document') or contains(text(), 'Upload Document to Vault')]/ancestor::div[contains(@class, 'bg-white')]")));
            Assert.True(modal.Displayed);

            var fileInput = Driver.FindElement(By.Id("evidence-file-input"));

            // SCENARIO A: Invalid non-PDF file type (.txt)
            string invalidTxtPath = CreateSyntheticTextFile("test_invalid_evidence", "Compliance rejection test: this is not a valid PDF file.");
            fileInput.SendKeys(invalidTxtPath);
            TestEnvironment.DemoPause(400);

            var fileTypeError = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'bg-rose-50')]//span[contains(text(), 'Compliance policy requires valid PDF')]")));
            Assert.True(fileTypeError.Displayed);

            var submitBtn = modal.FindElement(By.XPath(".//button[@type='submit' or contains(., 'Submit Evidence')]"));
            Assert.False(submitBtn.Enabled);

            // SCENARIO B: Oversized PDF (>5 MiB limit)
            string oversizedPdfPath = CreateOversizedSyntheticPdf("oversized_evidence", 5.4);
            fileInput.SendKeys(oversizedPdfPath);
            TestEnvironment.DemoPause(400);

            var oversizedError = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'bg-rose-50')]//span[contains(text(), 'exceeds the 5MB maximum limit')]")));
            Assert.True(oversizedError.Displayed);
            Assert.False(submitBtn.Enabled);

            // SCENARIO C: Valid PDF control state with required dates
            string validControlPdf = CreateSyntheticPdf("valid_compliance_control");
            fileInput.SendKeys(validControlPdf);
            TestEnvironment.DemoPause(300);

            var issueDateInput = modal.FindElement(By.XPath(".//label[contains(text(), 'Issue Date')]/following::input[1]"));
            var expiryDateInput = modal.FindElement(By.XPath(".//label[contains(text(), 'Expiry Date')]/following::input[1]"));

            SetDateInput(issueDateInput, DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM-dd"));
            SetDateInput(expiryDateInput, DateTime.UtcNow.AddYears(3).ToString("yyyy-MM-dd"));
            TestEnvironment.DemoPause(300);

            Wait.Until(d => submitBtn.Enabled);
            Assert.True(submitBtn.Enabled);

            // Safe Non-Mutating Close
            var cancelBtn = modal.FindElement(By.XPath(".//button[contains(text(), 'Cancel')]"));
            cancelBtn.Click();
            TestEnvironment.DemoPause(300);
        }
        finally
        {
            CleanupTempFiles();
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public void DOC02_MultiRole_DocumentRejection_Resubmission_AndVerification()
    {
        try
        {
            const string taskTitle = "Identity Verification (KYC Passport / ID)";
            const string rejectionReason = "QA Review: Passport photo is blurry. Please re-upload high-resolution scan.";
            const string verificationNote = "Replacement passport scan verified and matched with KYC database.";

            EnsureTaskIsReadyForTesting(taskTitle);

            // 1. Client uploads initial compliant synthetic evidence
            LoginViaUi(TestEnvironment.Stage2ClientEmail, TestEnvironment.Stage2ClientPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/portal"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            string initialPdf = CreateSyntheticPdf("KYC_Blurred_Sample");
            UploadClientEvidence(taskTitle, initialPdf, DateTime.UtcNow.AddMonths(-3).ToString("yyyy-MM-dd"), DateTime.UtcNow.AddYears(5).ToString("yyyy-MM-dd"));

            // 2. Staff inspects evidence and rejects with mandatory reason
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            var rejectBtn = Wait.Until(d =>
            {
                var card = d.FindElement(By.XPath($"//span[contains(text(), '{taskTitle}')]/ancestor::div[contains(@class, 'rounded-xl') or contains(@class, 'p-3.5')]"));
                var btn = card.FindElement(By.XPath(".//button[contains(., 'Reject') or @title='Reject evidence document']"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            Assert.NotNull(rejectBtn);
            rejectBtn.Click();
            TestEnvironment.DemoPause(300);

            var rejectModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Reject Stage Action Evidence')]/ancestor::div[contains(@class, 'bg-white')]")));
            var reasonInput = rejectModal.FindElement(By.XPath(".//textarea[contains(@placeholder, 'Scanned copy is blurry') or @required]"));
            reasonInput.Clear();
            reasonInput.SendKeys(rejectionReason);

            var confirmRejectBtn = rejectModal.FindElement(By.XPath(".//button[contains(., 'Confirm Rejection')]"));
            confirmRejectBtn.Click();
            TestEnvironment.DemoPause(600);

            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Reject Stage Action Evidence')]")).Count == 0);

            // 3. Client views rejection feedback alert and resubmits corrected PDF
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.Stage2ClientEmail, TestEnvironment.Stage2ClientPassword, TestEnvironment.TenantName);
            Wait.Until(d => d.Url.Contains("/portal"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            var revisionAlert = Wait.Until(d => d.FindElement(By.XPath($"//*[contains(text(), '{rejectionReason}') or contains(., 'Revision Requested')]")));
            Assert.True(revisionAlert.Displayed);

            string replacementPdf = CreateSyntheticPdf("KYC_Clear_Resubmission");
            UploadClientEvidence(taskTitle, replacementPdf, DateTime.UtcNow.AddMonths(-2).ToString("yyyy-MM-dd"), DateTime.UtcNow.AddYears(5).ToString("yyyy-MM-dd"));

            // 4. Staff verifies replacement evidence and confirms completion
            SwitchToFreshIndependentBrowserSession();
            LoginViaUi(TestEnvironment.StaffEmail, TestEnvironment.StaffPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
            Wait.Until(d => d.Url.Contains($"/workspace/{Stage2EngagementId}"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            var verifyBtn = Wait.Until(d =>
            {
                var card = d.FindElement(By.XPath($"//span[contains(text(), '{taskTitle}')]/ancestor::div[contains(@class, 'rounded-xl') or contains(@class, 'p-3.5')]"));
                var btn = card.FindElement(By.XPath(".//button[contains(., 'Verify Evidence')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            Assert.NotNull(verifyBtn);
            verifyBtn.Click();
            TestEnvironment.DemoPause(300);

            var verifyModal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Verify') and contains(text(), 'Evidence')]/ancestor::div[contains(@class, 'bg-white')]")));
            var notesInput = verifyModal.FindElement(By.XPath(".//textarea[contains(@placeholder, 'Document details') or contains(@placeholder, 'regulatory compliance')]"));
            notesInput.Clear();
            notesInput.SendKeys(verificationNote);

            var confirmVerifyBtn = verifyModal.FindElement(By.XPath(".//button[contains(., 'Confirm Verification')]"));
            confirmVerifyBtn.Click();
            TestEnvironment.DemoPause(600);

            Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Verify') and contains(text(), 'Evidence')]")).Count == 0);

            // Assert task card displays Verified
            Wait.Until(d =>
            {
                var card = d.FindElement(By.XPath($"//span[contains(text(), '{taskTitle}')]/ancestor::div[contains(@class, 'rounded-xl') or contains(@class, 'p-3.5')]"));
                return card.Text.Contains("Verified", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception)
        {
            CaptureScreenshot("DOC02_Failure");
            throw;
        }
        finally
        {
            EnsureTaskIsReadyForTesting("Identity Verification (KYC Passport / ID)");
            CleanupTempFiles();
        }
    }

    #region Helper Methods

    private void UploadClientEvidence(string taskTitle, string pdfPath, string issueDate, string expiryDate)
    {
        var uploadBtn = Wait.Until(d =>
        {
            // 1. Try matching hero section card
            var heroCards = d.FindElements(By.XPath($"//*[self::h2 or self::h3][contains(., '{taskTitle}')]/ancestor::div[contains(@class, 'rounded-2xl')][1]"));
            if (heroCards.Count > 0)
            {
                var buttons = heroCards[0].FindElements(By.XPath(".//button[contains(., 'Upload') or contains(., 'upload') or contains(., 'Evidence')]"));
                var btn = buttons.FirstOrDefault(b => b.Enabled);
                if (btn != null) return btn;
            }

            // 2. Try matching pending action list card
            var listCards = d.FindElements(By.XPath($"//*[contains(., '{taskTitle}')]/ancestor::div[contains(@class, 'p-3.5') or contains(@class, 'rounded-xl')][1]"));
            if (listCards.Count > 0)
            {
                var buttons = listCards[0].FindElements(By.XPath(".//button[contains(., 'Upload') or contains(., 'upload') or contains(., 'Evidence')]"));
                var btn = buttons.FirstOrDefault(b => b.Enabled);
                if (btn != null) return btn;
            }

            // 3. Fallback: any card mentioning task title with an upload button
            var fallbackCards = d.FindElements(By.XPath($"//*[contains(., '{taskTitle}')]/ancestor::div[contains(@class, 'border')][1]"));
            if (fallbackCards.Count > 0)
            {
                var buttons = fallbackCards[0].FindElements(By.XPath(".//button[contains(., 'Upload') or contains(., 'upload') or contains(., 'Evidence')]"));
                var btn = buttons.FirstOrDefault(b => b.Enabled);
                if (btn != null) return btn;
            }

            return null;
        });

        Assert.NotNull(uploadBtn);
        ((IJavaScriptExecutor)Driver).ExecuteScript("arguments[0].scrollIntoView({block: 'center', inline: 'center'});", uploadBtn);
        TestEnvironment.DemoPause(300);
        uploadBtn.Click();
        TestEnvironment.DemoPause(300);

        var modal = Wait.Until(d => d.FindElement(By.XPath("//h3[contains(text(), 'Upload Evidence Document') or contains(text(), 'Upload')]/ancestor::div[contains(@class, 'bg-white')]")));
        var fileInput = Driver.FindElement(By.Id("evidence-file-input"));
        fileInput.SendKeys(pdfPath);
        TestEnvironment.DemoPause(300);

        var issueDateInput = modal.FindElement(By.XPath(".//label[contains(text(), 'Issue Date')]/following::input[1]"));
        SetDateInput(issueDateInput, issueDate);

        var expiryDateInput = modal.FindElement(By.XPath(".//label[contains(text(), 'Expiry Date')]/following::input[1]"));
        SetDateInput(expiryDateInput, expiryDate);
        TestEnvironment.DemoPause(300);

        var submitBtn = modal.FindElement(By.XPath(".//button[contains(., 'Submit Evidence') or @type='submit']"));
        submitBtn.Click();
        TestEnvironment.DemoPause(600);

        Wait.Until(d => d.FindElements(By.XPath("//h3[contains(text(), 'Upload Evidence Document')]")).Count == 0);
    }

    private void SetDateInput(IWebElement element, string yyyyMmDd)
    {
        ((IJavaScriptExecutor)Driver).ExecuteScript(
            "const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set; " +
            "if (setter) { setter.call(arguments[0], arguments[1]); } else { arguments[0].value = arguments[1]; } " +
            "arguments[0].dispatchEvent(new Event('input', { bubbles: true })); " +
            "arguments[0].dispatchEvent(new Event('change', { bubbles: true }));",
            element, yyyyMmDd);
    }

    private string CreateSyntheticPdf(string prefix)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.pdf");
        string pdfContent =
            "%PDF-1.4\n" +
            "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
            "3 0 obj<</Type/Page/MediaBox[0 0 612 792]/Parent 2 0 R/Resources<<>>>>endobj\n" +
            "xref\n0 4\n0000000000 65535 f\n0000000010 00000 n\n0000000060 00000 n\n0000000117 00000 n\n" +
            "trailer<</Size 4/Root 1 0 R>>\nstartxref\n205\n%%EOF\n";

        File.WriteAllBytes(filePath, Encoding.ASCII.GetBytes(pdfContent));
        _tempFiles.Add(filePath);
        return filePath;
    }

    private string CreateSyntheticTextFile(string prefix, string content)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.txt");
        File.WriteAllText(filePath, content);
        _tempFiles.Add(filePath);
        return filePath;
    }

    private string CreateOversizedSyntheticPdf(string prefix, double sizeMb)
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.pdf");
        int targetBytes = (int)(sizeMb * 1024 * 1024);

        using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
        {
            byte[] header = Encoding.ASCII.GetBytes("%PDF-1.4\n%Oversized compliance validation test fixture\n");
            fs.Write(header, 0, header.Length);

            byte[] chunk = new byte[64 * 1024];
            int written = header.Length;
            while (written < targetBytes)
            {
                int toWrite = Math.Min(chunk.Length, targetBytes - written);
                fs.Write(chunk, 0, toWrite);
                written += toWrite;
            }
        }

        _tempFiles.Add(filePath);
        return filePath;
    }

    private void CleanupTempFiles()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }
        _tempFiles.Clear();
    }

    private void EnsureTaskIsReadyForTesting(string taskTitle)
    {
        try
        {
            TestEnvironment.Initialize();
            using var client = new HttpClient();
            var authBody = JsonSerializer.Serialize(new
            {
                email = TestEnvironment.StaffEmail,
                password = TestEnvironment.StaffPassword
            });
            var loginContent = new StringContent(authBody, Encoding.UTF8, "application/json");
            var loginResp = client.PostAsync($"{TestEnvironment.IdentityApiUrl}/api/auth/login", loginContent).GetAwaiter().GetResult();
            Console.WriteLine($"[EnsureTask] Login: {loginResp.StatusCode}");
            if (!loginResp.IsSuccessStatusCode) return;

            var loginJson = JsonDocument.Parse(loginResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var globalToken = loginJson.RootElement.GetProperty("token").GetString();

            using var tenantReq = new HttpRequestMessage(HttpMethod.Get, $"{TestEnvironment.IdentityApiUrl}/api/tenant/mine");
            tenantReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", globalToken);
            var tenantResp = client.SendAsync(tenantReq).GetAwaiter().GetResult();
            Console.WriteLine($"[EnsureTask] TenantMine: {tenantResp.StatusCode}");
            if (!tenantResp.IsSuccessStatusCode) return;

            var tenantJson = JsonDocument.Parse(tenantResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            string? tenantId = null;
            if (tenantJson.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tenantJson.RootElement.EnumerateArray())
                {
                    if (t.TryGetProperty("tenantId", out var tp))
                    {
                        tenantId = tp.GetString();
                        if (t.TryGetProperty("name", out var np) &&
                            string.Equals(np.GetString(), TestEnvironment.TenantName, StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                }
            }
            else if (tenantJson.RootElement.TryGetProperty("tenantId", out var tp))
            {
                tenantId = tp.GetString();
            }

            Console.WriteLine($"[EnsureTask] TenantId: {tenantId}");
            if (string.IsNullOrEmpty(tenantId)) return;

            using var wsReq = new HttpRequestMessage(HttpMethod.Post, $"{TestEnvironment.IdentityApiUrl}/api/auth/select-workspace/{tenantId}");
            wsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", globalToken);
            var wsResp = client.SendAsync(wsReq).GetAwaiter().GetResult();
            Console.WriteLine($"[EnsureTask] SelectWs: {wsResp.StatusCode}");
            if (!wsResp.IsSuccessStatusCode) return;

            var wsJson = JsonDocument.Parse(wsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var wsToken = wsJson.RootElement.GetProperty("token").GetString();

            using var actionsReq = new HttpRequestMessage(HttpMethod.Get, $"{TestEnvironment.WorkflowApiUrl}/api/engagements/{Stage2EngagementId}/actions?tenantId={tenantId}");
            actionsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
            var actionsResp = client.SendAsync(actionsReq).GetAwaiter().GetResult();
            Console.WriteLine($"[EnsureTask] GetActions: {actionsResp.StatusCode}");
            if (!actionsResp.IsSuccessStatusCode) return;

            var actionsJson = JsonDocument.Parse(actionsResp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            foreach (var element in actionsJson.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("title", out var titleProp) &&
                    string.Equals(titleProp.GetString(), taskTitle, StringComparison.OrdinalIgnoreCase))
                {
                    string status = element.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                    Console.WriteLine($"[EnsureTask] Task '{taskTitle}' current status: {status}");
                    if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
                    {
                        var actionId = element.GetProperty("actionId").GetString();
                        var verifyBody = JsonSerializer.Serialize(new
                        {
                            verificationStatus = "Rejected",
                            verifiedBy = "StaffUser",
                            verificationReason = "Reset for test"
                        });
                        using var putReq = new HttpRequestMessage(HttpMethod.Put, $"{TestEnvironment.WorkflowApiUrl}/api/engagements/{Stage2EngagementId}/actions/{actionId}/verification?tenantId={tenantId}");
                        putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
                        putReq.Content = new StringContent(verifyBody, Encoding.UTF8, "application/json");
                        var putResp = client.SendAsync(putReq).GetAwaiter().GetResult();
                        Console.WriteLine($"[EnsureTask] Reset action status: {putResp.StatusCode}");
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EnsureTask] Exception: {ex.Message}");
        }
    }

    #endregion
}
