using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenQA.Selenium;
using Xunit;

namespace Custodian.System.Tests;

public class ReportingTests : TestBase
{
    [Fact]
    public async Task TC08D_ReportsPage_Filters_And_Pdf_Csv_Downloads()
    {
        // 1. Prepare workspace session
        var ws = await QaApiHelper.RegisterAndSetupWorkspaceAsync("S3-Reports");
        InjectRealAuthSession(ws.Token, ws.TenantId, ws.TenantName);

        // 2. Open /reports
        Driver.Navigate().GoToUrl($"{BaseUrl}/reports");

        // Verify page header and filter controls render
        var pageTitle = Wait.Until(d => d.FindElement(By.XPath("//h1[contains(text(), 'SLA Performance Report') or contains(text(), 'Reports')]")));
        Assert.True(pageTitle.Displayed);

        var dateInput = Wait.Until(d => d.FindElement(By.XPath("//input[@type='date']")));
        Assert.True(dateInput.Displayed);

        var engagementSelect = Driver.FindElement(By.Id("sla-engagement"));
        Assert.True(engagementSelect.Displayed);

        var stageSelect = Driver.FindElement(By.Id("sla-stage"));
        Assert.True(stageSelect.Displayed);

        CaptureEvidence("09-reports-page.png");

        // 3. Trigger PDF download
        var pdfBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Download PDF') or contains(., 'PDF')]")));
        pdfBtn.Click();

        // Assert PDF file is downloaded to DownloadDir
        var pdfFile = await WaitForDownloadedFileAsync("*.pdf", TimeSpan.FromSeconds(15));
        Assert.NotNull(pdfFile);
        var pdfFileInfo = new FileInfo(pdfFile);
        Assert.True(pdfFileInfo.Length > 0, "Downloaded PDF file must have non-zero length.");
        Assert.Equal(".pdf", pdfFileInfo.Extension.ToLower());

        // 4. Trigger CSV download
        var csvBtn = Wait.Until(d => d.FindElement(By.XPath("//button[contains(., 'Download CSV') or contains(., 'CSV')]")));
        csvBtn.Click();

        // Assert CSV file is downloaded to DownloadDir
        var csvFile = await WaitForDownloadedFileAsync("*.csv", TimeSpan.FromSeconds(15));
        Assert.NotNull(csvFile);
        var csvFileInfo = new FileInfo(csvFile);
        Assert.True(csvFileInfo.Length > 0, "Downloaded CSV file must have non-zero length.");
        Assert.Equal(".csv", csvFileInfo.Extension.ToLower());

        CaptureEvidence("10-report-download-proof.png");
    }

    private async Task<string?> WaitForDownloadedFileAsync(string searchPattern, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            var files = Directory.GetFiles(DownloadDir, searchPattern);
            // Ignore incomplete downloads (e.g. .crdownload)
            var completeFile = files.FirstOrDefault(f => !f.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase));
            if (completeFile != null)
            {
                // Ensure write completion
                try
                {
                    using var stream = File.Open(completeFile, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (stream.Length > 0) return completeFile;
                }
                catch (IOException)
                {
                    // File still being written by Chrome
                }
            }
            await Task.Delay(500);
        }
        return null;
    }
}
