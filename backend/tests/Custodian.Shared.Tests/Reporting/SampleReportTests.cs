using System.Diagnostics.Metrics;
using System.Text;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Observability;
using Custodian.Shared.Reporting.Rendering;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using Xunit;
using static Custodian.Shared.Tests.Reporting.SampleReportModelBuilder;

namespace Custodian.Shared.Tests.Reporting;

/// <summary>
/// CSTD-36-4/36-6: the sample report through the shared pipeline, and generation telemetry.
/// These run in CI (dotnet test on the solution), so a renderer that throws on the sample model
/// fails the build.
/// </summary>
public class SampleReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 10, 30, 0, TimeSpan.Zero);

    private readonly PdfReportRenderer _renderer = new();
    private readonly CsvExporter _csv = new();

    private static readonly SampleTask[] Tasks =
    [
        new("1 Onboarding", true, 10),
        new("1 Onboarding", false, 20),
        new("2 Documents", true, 5)
    ];

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return new string(string.Concat(document.GetPages().Select(p => p.Text)).Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    [Fact]
    public void SampleReport_RendersAsPdfAndCsv_WithTheSharedComponents()
    {
        var model = Build(Tasks, Now);

        var pdf = _renderer.RenderPdf(model);
        var csv = _csv.ToCsv(model.Detail!);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Equal(4, Encoding.UTF8.GetString(csv).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length); // header + 3 tasks
        Assert.Equal("custodian-sample_tasks-20260928-1030Z.pdf", ReportResults.FileName(model.Metadata, ReportFormat.Pdf));
    }

    [Fact]
    public void Numbers_ComeFromTheData()
    {
        var text = PdfText(_renderer.RenderPdf(Build(Tasks, Now)));

        Assert.Contains("Totaltasks3", text);
        Assert.Contains("Completed2", text);
        Assert.Contains("66.7%", text);
        Assert.Contains("1Onboarding215", text); // 2 tasks, average (10 + 20) / 2 = 15 hours
    }

    [Fact]
    public void ChangingTheData_ChangesTheReport()
    {
        var before = PdfText(_renderer.RenderPdf(Build(Tasks, Now)));
        var after = PdfText(_renderer.RenderPdf(Build([.. Tasks, new SampleTask("2 Documents", true, 7)], Now)));

        Assert.Contains("Totaltasks3", before);
        Assert.Contains("Totaltasks4", after);
        Assert.Contains("75.0%", after);
    }

    [Fact]
    public void NoData_IsAValidReport_WithZerosAndTheEmptyMessage()
    {
        var model = Build([], Now, stageFilter: "5 Closure");

        var text = PdfText(_renderer.RenderPdf(model));

        Assert.Contains("Totaltasks0", text);
        Assert.Contains("n/a", text);
        Assert.Contains("Norecordsmatchtheselectedfilters.", text);
        Assert.Null(model.Detail);
    }

    // ---------------- Telemetry ----------------

    private sealed class CapturingLogger : ILogger<ReportTelemetry>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class MeasurementCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Values { get; } = [];

        public MeasurementCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ReportTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
            _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
            _listener.Start();
        }

        private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var map = new Dictionary<string, object?>();
            foreach (var tag in tags) map[tag.Key] = tag.Value;
            // Other tests may run reports in parallel; keep only this test's report code.
            if (Equals(map.GetValueOrDefault("report_code"), TelemetryCode)) lock (Values) Values.Add((instrument.Name, value, map));
        }

        public void Dispose() => _listener.Dispose();
    }

    private const string TelemetryCode = "TELEMETRY_TEST";

    [Fact]
    public async Task Telemetry_RecordsSuccessWithDurationAndSize()
    {
        using var capture = new MeasurementCapture();
        var logger = new CapturingLogger();
        var telemetry = new ReportTelemetry(logger);
        var model = Build(Tasks, Now);

        var output = await telemetry.MeasureAsync(TelemetryCode, ReportFormat.Pdf, () =>
            Task.FromResult(new ReportRun(ReportResults.Output(model.Metadata, ReportFormat.Pdf, _renderer.RenderPdf(model)))));

        var duration = Assert.Single(capture.Values, v => v.Instrument == "custodian.report.generation.duration");
        Assert.Equal("success", duration.Tags["outcome"]);
        Assert.Equal("pdf", duration.Tags["format"]);
        var size = Assert.Single(capture.Values, v => v.Instrument == "custodian.report.generation.size");
        Assert.Equal(output.Content.Length, size.Value);
        Assert.Contains(logger.Lines, l => l.Contains(TelemetryCode) && l.Contains("success"));
    }

    [Fact]
    public void Telemetry_RecordsEmptyRuns()
    {
        using var capture = new MeasurementCapture();
        var telemetry = new ReportTelemetry(new CapturingLogger());

        telemetry.Measure(TelemetryCode, ReportFormat.Csv, () =>
            new ReportRun(new Custodian.Shared.Reporting.Reports.ReportOutput("x.csv", [], "text/csv"), IsEmpty: true));

        Assert.Equal("empty", Assert.Single(capture.Values, v => v.Instrument == "custodian.report.generation.duration").Tags["outcome"]);
    }

    [Theory]
    [InlineData(ReportErrorKind.InvalidFilter, "invalid_filter")]
    [InlineData(ReportErrorKind.SubjectNotFound, "subject_not_found")]
    [InlineData(ReportErrorKind.DataSourceUnavailable, "data_source_unavailable")]
    public async Task Telemetry_RecordsTheErrorKind_AndRethrows(ReportErrorKind kind, string outcome)
    {
        using var capture = new MeasurementCapture();
        var telemetry = new ReportTelemetry(new CapturingLogger());
        var error = new ReportGenerationException(kind, "Client 'Acme Advisory' not found.");

        var thrown = await Assert.ThrowsAsync<ReportGenerationException>(() =>
            telemetry.MeasureAsync(TelemetryCode, ReportFormat.Pdf, () => throw error));

        Assert.Same(error, thrown);
        var duration = Assert.Single(capture.Values, v => v.Instrument == "custodian.report.generation.duration");
        Assert.Equal(outcome, duration.Tags["outcome"]);
        Assert.DoesNotContain(capture.Values, v => v.Instrument == "custodian.report.generation.size");
    }

    [Fact]
    public async Task Telemetry_UnexpectedFailure_IsGenerationFailed_AndLogsNoReportData()
    {
        using var capture = new MeasurementCapture();
        var logger = new CapturingLogger();
        var telemetry = new ReportTelemetry(logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            telemetry.MeasureAsync(TelemetryCode, ReportFormat.Pdf, () => throw new InvalidOperationException("Acme Advisory")));

        Assert.Equal("generation_failed", Assert.Single(capture.Values, v => v.Instrument == "custodian.report.generation.duration").Tags["outcome"]);
        Assert.DoesNotContain(logger.Lines, l => l.Contains("Acme"));
    }
}
