using System.Text;
using Custodian.Shared.Reporting.Reports;

namespace Custodian.Shared.Tests;

public sealed class ReportGeneratorTests
{
    private sealed record Engagement(string Client, decimal Amount);

    [Fact]
    public void GenerateCsv_UsesConfiguredColumnsAndEscapesValues()
    {
        var definition = new ReportDefinition<Engagement>(
            "engagements",
            [
                new("Client", engagement => engagement.Client),
                new("Amount", engagement => engagement.Amount)
            ]);

        var result = new ReportGenerator().GenerateCsv(
            definition,
            [new Engagement("Acme, Inc.", 1250.50m)]);

        Assert.Equal("engagements", result.Name);
        Assert.Equal("text/csv; charset=utf-8", result.ContentType);
        Assert.Equal(
            "Client,Amount\r\n\"Acme, Inc.\",1250.50\r\n",
            Encoding.UTF8.GetString(result.Content));
    }

    [Fact]
    public void GenerateJson_UsesConfiguredColumnHeaders()
    {
        var definition = new ReportDefinition<Engagement>(
            "engagements",
            [new("Client name", engagement => engagement.Client)]);

        var result = new ReportGenerator().GenerateJson(
            definition,
            [new Engagement("Acme", 1250.50m)]);

        Assert.Equal("application/json; charset=utf-8", result.ContentType);
        Assert.Equal("[{\"Client name\":\"Acme\"}]", Encoding.UTF8.GetString(result.Content));
    }
}