using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using UglyToad.PdfPig;
using Xunit;

namespace Custodian.System.Tests;

/// <summary>
/// Streamlined Workflow 6: Cryptographic Audit Trail, PDF Reports, and Security Boundaries.
/// Validates Audit microservice SHA-256 hash chains, PdfPig binary and AST PDF report validation,
/// and negative cross-client IDOR / cross-tenant authorization boundaries.
/// </summary>
public class AuditAndSecurityTests : TestBase
{
    private readonly List<string> _tempDirs = new();

    private const string BaselineEngagementId = "2767cc0a-edf6-4143-bf80-1f4cc2a3a065";
    private const string ClosedEngagementId = "015bfe04-cb20-44c5-8be0-ccb4be30b18f";
    private const string Stage2EngagementId = "4ca380d3-04e0-4be5-8e6a-6f8a760803b6";

    private const string WorkflowApiBase = "http://localhost:5225";
    private const string DocumentsApiBase = "http://localhost:5171";
    private const string AuditApiBase = "http://localhost:5051";

    [Fact]
    [Trait("Category", "CI")]
    public void AUDIT01_TamperEvidentHashChainVerification()
    {
        try
        {
            // 1. Authenticate with real QA Owner credentials and open /audit
            LoginViaUi(TestEnvironment.OwnerEmail, TestEnvironment.OwnerPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/audit");
            Wait.Until(d => d.Url.Contains("/audit"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            // 2. Assert Audit Log header, telemetry pill, and event counter
            var heading = Wait.Until(d => d.FindElement(By.XPath("//h1[contains(text(), 'Audit Log')]")));
            Assert.True(heading.Displayed);

            var telemetryPill = Driver.FindElement(By.XPath("//span[contains(text(), 'TAMPER-EVIDENT AUDIT TRAIL')]"));
            Assert.True(telemetryPill.Displayed);

            var eventsKpi = Driver.FindElement(By.XPath("//span[contains(text(), 'Events')]/ancestor::div[contains(@class, 'p-5')]//div[contains(@class, 'text-2xl')]"));
            int eventCount = int.Parse(eventsKpi.Text.Trim());
            Assert.True(eventCount > 0, "Audit trail must have at least one recorded event for the tenant.");

            // 3. Select baseline engagement from filter dropdown
            var filterSelectEl = Wait.Until(d => d.FindElement(By.CssSelector("select[aria-label='Filter by engagement']")));
            var filterSelect = new SelectElement(filterSelectEl);

            var baselineOption = filterSelect.Options.FirstOrDefault(o => o.Text.Contains("E2E Test Client", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(baselineOption);
            filterSelect.SelectByValue(baselineOption.GetAttribute("value") ?? "");
            TestEnvironment.DemoPause(400);

            // 4. Expand event details to verify SHA-256 cryptographic hash
            var eventLabel = Wait.Until(d => d.FindElement(By.XPath("//span[contains(text(), 'Engagement created')]")));
            var eventButton = eventLabel.FindElement(By.XPath("./ancestor::button"));
            eventButton.Click();
            TestEnvironment.DemoPause(300);

            var techDetailsSummary = Wait.Until(d => d.FindElement(By.XPath("//summary[contains(text(), 'Technical details')]")));
            techDetailsSummary.Click();
            TestEnvironment.DemoPause(300);

            var hashElement = Wait.Until(d => d.FindElement(By.XPath("//div[contains(@class, 'font-mono') and contains(., 'hash')]")));
            Assert.True(hashElement.Displayed);
            Assert.Contains("c5b7cf3c4a608d1f7c8413dcf3e2260a3ba8fc5ee7084a164e5ab38d76d10464", hashElement.Text);

            // 5. Trigger live UI chain-verification control and assert transition to Intact
            var verifyBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Verify this engagement')]")));
            verifyBtn.Click();
            TestEnvironment.DemoPause(400);

            Wait.Until(d => verifyBtn.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            Wait.Until(d =>
            {
                var kpi = d.FindElement(By.XPath("//span[contains(text(), 'Hash chains')]/ancestor::div[contains(@class, 'p-5')]//div[contains(@class, 'text-2xl')]"));
                return kpi.Text.Contains("Intact");
            });

            Assert.Empty(Driver.FindElements(By.XPath("//div[contains(text(), 'Tampering detected')]")));
        }
        catch (Exception)
        {
            CaptureScreenshot("AUDIT01_Failure");
            throw;
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public void REPORT01_ProofOfDeliveryPdfDownloadAndAstValidation()
    {
        string downloadDir = Path.Combine(Path.GetTempPath(), $"custodian_pdf_reports_{Guid.NewGuid():N}");
        _tempDirs.Add(downloadDir);

        try
        {
            // 1. Launch automatic download configured browser session
            SwitchToDownloadEnabledBrowserSession(downloadDir);

            // 2. Authenticate as Owner and navigate to /reports
            LoginViaUi(TestEnvironment.OwnerEmail, TestEnvironment.OwnerPassword, TestEnvironment.TenantName);
            Driver.Navigate().GoToUrl($"{BaseUrl}/reports");
            Wait.Until(d => d.Url.Contains("/reports"));
            Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
            TestEnvironment.DemoPause(400);

            // 3. Locate Proof of Delivery section and select verified closed engagement
            var podHeader = Wait.Until(d => d.FindElement(By.XPath("//h2[contains(text(), 'Proof of Delivery')]")));
            Assert.True(podHeader.Displayed);

            var podSelectEl = Wait.Until(d => d.FindElement(By.Id("pod-engagement")));
            var podSelect = new SelectElement(podSelectEl);
            podSelect.SelectByValue(ClosedEngagementId);
            TestEnvironment.DemoPause(400);

            // 4. Download Proof of Delivery PDF
            var downloadPodBtn = Wait.Until(d =>
            {
                var btn = d.FindElement(By.XPath("//button[contains(., 'Download Proof of Delivery')]"));
                return btn.Displayed && btn.Enabled ? btn : null;
            });
            Assert.NotNull(downloadPodBtn);
            downloadPodBtn.Click();
            TestEnvironment.DemoPause(1000);

            // 5. Await downloaded PDF file
            string podPdfFile = WaitForDownloadedPdf(downloadDir, timeoutSeconds: 30);
            Assert.True(File.Exists(podPdfFile), "Proof of Delivery PDF must exist on disk.");
            Assert.True(new FileInfo(podPdfFile).Length > 0, "File must not be empty.");

            // 6. Verify binary signature (%PDF-)
            byte[] podBytes = File.ReadAllBytes(podPdfFile);
            string podHeaderString = Encoding.ASCII.GetString(podBytes.Take(5).ToArray());
            Assert.Equal("%PDF-", podHeaderString);

            // 7. Validate PDF structure and textual content using PdfPig
            using (var podPdf = PdfDocument.Open(podPdfFile))
            {
                Assert.True(podPdf.NumberOfPages >= 1, "Proof of Delivery must contain at least 1 page.");

                string extractedPodText = string.Join(" ", podPdf.GetPages().Select(p => p.Text));
                Assert.Contains("Proof of Delivery", extractedPodText);
                Assert.Contains("Custodian", extractedPodText);
                Assert.True(
                    extractedPodText.Contains("Tenant", StringComparison.OrdinalIgnoreCase) ||
                    extractedPodText.Contains(TestEnvironment.TenantName, StringComparison.OrdinalIgnoreCase),
                    $"PDF report must contain tenant identification. Extracted: {extractedPodText}");

                bool hasHashInfo = extractedPodText.Contains("Hash", StringComparison.OrdinalIgnoreCase) ||
                                  extractedPodText.Contains("SHA-256", StringComparison.OrdinalIgnoreCase) ||
                                  extractedPodText.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
                                  extractedPodText.Contains("Verified", StringComparison.OrdinalIgnoreCase);
                Assert.True(hasHashInfo, "Report must contain audit or cryptographic verification details.");
            }
        }
        finally
        {
            CleanupTempDirectories();
        }
    }

    [Fact]
    [Trait("Category", "CI")]
    public async Task SEC01_CrossClientAndCrossTenantAuthorization()
    {
        // 1. Authenticate as QA Client into UI and extract valid session JWT
        LoginViaUi(TestEnvironment.ClientEmail, TestEnvironment.ClientPassword, TestEnvironment.TenantName);
        Wait.Until(d => d.Url.Contains("/portal"));
        Wait.Until(d => d.FindElements(By.CssSelector(".animate-spin")).Count == 0);
        TestEnvironment.DemoPause(400);

        string clientToken = ((IJavaScriptExecutor)Driver).ExecuteScript("return localStorage.getItem('custodian_token');") as string ?? "";
        Assert.False(string.IsNullOrWhiteSpace(clientToken), "Client must have an active JWT in browser localStorage.");

        // Positive Control: Client successfully accesses own engagement
        var (ownStatus, ownBody) = await SendApiRequestAsync(
            HttpMethod.Get,
            $"{WorkflowApiBase}/api/portal/my-engagement?tenantId=17d31dd0-56e0-4f14-bf62-5df6db779586",
            clientToken
        );
        Assert.Equal(200, ownStatus);
        Assert.Contains(BaselineEngagementId, ownBody);

        // Security Check 1: UI Route Guard redirects Client attempting to access another engagement workspace
        Driver.Navigate().GoToUrl($"{BaseUrl}/workspace/{Stage2EngagementId}");
        Wait.Until(d => d.Url.Contains("/portal"));
        Assert.Contains("/portal", Driver.Url);
        TestEnvironment.DemoPause(300);

        // Security Check 2: API IDOR Denial - Client attempts to read foreign engagement dashboard (HTTP 403)
        var (clientBEngStatus, clientBEngBody) = await SendApiRequestAsync(
            HttpMethod.Get,
            $"{WorkflowApiBase}/api/portal/engagements/{Stage2EngagementId}?tenantId=17d31dd0-56e0-4f14-bf62-5df6db779586",
            clientToken
        );
        Assert.Equal(403, clientBEngStatus);
        Assert.Contains("Access denied: Clients can only access their own engagement", clientBEngBody);

        // Security Check 3: Forged tenant query parameter conflicting with JWT claim is rejected (HTTP 403)
        const string foreignTenantId = "2f0557d4-45cf-4b62-84d5-c03486405f9a";
        var (forgedTenantStatus, _) = await SendApiRequestAsync(
            HttpMethod.Get,
            $"{WorkflowApiBase}/api/portal/my-engagement?tenantId={foreignTenantId}",
            clientToken
        );
        Assert.Equal(403, forgedTenantStatus);

        // Security Check 4: Client role forbidden from Staff document verification endpoint (HTTP 403)
        var (verifyRoleStatus, _) = await SendApiRequestAsync(
            HttpMethod.Post,
            $"{DocumentsApiBase}/api/engagements/{BaselineEngagementId}/documents/00000000-0000-0000-0000-000000000001/verify",
            clientToken,
            new StringContent("{\"notes\":\"unauthorized\"}", Encoding.UTF8, "application/json")
        );
        Assert.Equal(403, verifyRoleStatus);
    }

    #region Helper Methods

    private string WaitForDownloadedPdf(string directory, int timeoutSeconds)
    {
        var timeout = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < timeout)
        {
            if (Directory.Exists(directory))
            {
                var files = Directory.GetFiles(directory, "*.pdf")
                    .Where(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (files.Count > 0)
                {
                    var file = files.First();
                    try
                    {
                        using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None);
                        if (stream.Length > 0) return file;
                    }
                    catch (IOException) { }
                }
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException($"Timed out waiting for PDF download in '{directory}' after {timeoutSeconds} seconds.");
    }

    private static async Task<(int StatusCode, string ResponseBody)> SendApiRequestAsync(
        HttpMethod method,
        string url,
        string? bearerToken = null,
        HttpContent? content = null)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(method, url);

        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        if (content != null)
        {
            request.Content = content;
        }

        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body);
    }

    private void CleanupTempDirectories()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
        _tempDirs.Clear();
    }

    #endregion
}
