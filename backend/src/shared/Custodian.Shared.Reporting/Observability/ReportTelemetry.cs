using System.Diagnostics;
using System.Diagnostics.Metrics;
using Custodian.Shared.Observability;
using Custodian.Shared.Reporting.Errors;
using Custodian.Shared.Reporting.Export;
using Custodian.Shared.Reporting.Reports;
using Microsoft.Extensions.Logging;

namespace Custodian.Shared.Reporting.Observability;

/// <summary>What a report run produced: the file, and whether no records matched the filters.</summary>
public sealed record ReportRun(ReportOutput Output, bool IsEmpty = false);

/// <summary>
/// CSTD-36-6: one record per report generation: report code, format, duration, outcome and size.
/// Written three ways, following the next-action engine's pattern (no exporter is wired yet):
/// - a structured log line (App Service log stream / App Insights pick up ILogger output);
/// - the <see cref="MeterName"/> meter: <c>custodian.report.generation.duration</c> (ms) and
///   <c>custodian.report.generation.size</c> (bytes), tagged report_code, format, outcome;
/// - a <c>report.generate</c> activity on the "Custodian.Reporting" source.
/// Never records report contents or filter values: those can identify clients.
/// </summary>
public sealed class ReportTelemetry
{
    public const string MeterName = "Custodian.Reporting";

    public const string OutcomeSuccess = "success";
    public const string OutcomeEmpty = "empty";

    private static readonly ActivitySource Source = Telemetry.CreateSource("Reporting");
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "custodian.report.generation.duration", unit: "ms", description: "Time to generate a report, from query to file.");
    private static readonly Histogram<long> Size = Meter.CreateHistogram<long>(
        "custodian.report.generation.size", unit: "By", description: "Size of generated report files.");

    private readonly ILogger<ReportTelemetry> _logger;

    public ReportTelemetry(ILogger<ReportTelemetry> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Runs the whole generation (query, model, render) and records it. Exceptions are recorded with
    /// their error kind and rethrown unchanged for the <see cref="ReportExceptionFilter"/>.
    /// </summary>
    public async Task<ReportOutput> MeasureAsync(string reportCode, ReportFormat format, Func<Task<ReportRun>> generate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportCode);
        ArgumentNullException.ThrowIfNull(generate);

        using var activity = Source.StartActivity("report.generate");
        activity?.SetTag("report.code", reportCode);
        activity?.SetTag("report.format", FormatTag(format));
        var started = Stopwatch.GetTimestamp();

        try
        {
            var run = await generate();
            var outcome = run.IsEmpty ? OutcomeEmpty : OutcomeSuccess;
            Record(activity, reportCode, format, outcome, Stopwatch.GetElapsedTime(started), run.Output.Content.Length);
            return run.Output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var kind = (ex as ReportGenerationException)?.Kind ?? ReportErrorKind.GenerationFailed;
            Record(activity, reportCode, format, OutcomeFor(kind), Stopwatch.GetElapsedTime(started), sizeBytes: null);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public ReportOutput Measure(string reportCode, ReportFormat format, Func<ReportRun> generate)
    {
        ArgumentNullException.ThrowIfNull(generate);
        return MeasureAsync(reportCode, format, () => Task.FromResult(generate())).GetAwaiter().GetResult();
    }

    /// <summary>snake_case outcome tag for an error kind, e.g. <c>invalid_filter</c>.</summary>
    public static string OutcomeFor(ReportErrorKind kind) => kind switch
    {
        ReportErrorKind.InvalidFilter => "invalid_filter",
        ReportErrorKind.Forbidden => "forbidden",
        ReportErrorKind.SubjectNotFound => "subject_not_found",
        ReportErrorKind.DataSourceUnavailable => "data_source_unavailable",
        _ => "generation_failed"
    };

    private void Record(Activity? activity, string reportCode, ReportFormat format, string outcome, TimeSpan elapsed, long? sizeBytes)
    {
        var tags = new TagList
        {
            { "report_code", reportCode },
            { "format", FormatTag(format) },
            { "outcome", outcome }
        };
        Duration.Record(elapsed.TotalMilliseconds, tags);
        if (sizeBytes.HasValue) Size.Record(sizeBytes.Value, tags);

        activity?.SetTag("report.outcome", outcome);
        if (sizeBytes.HasValue) activity?.SetTag("report.size_bytes", sizeBytes.Value);

        _logger.LogInformation(
            "Report {ReportCode} ({Format}) finished in {DurationMs} ms: {Outcome}, {SizeBytes} bytes.",
            reportCode, FormatTag(format), Math.Round(elapsed.TotalMilliseconds, 1), outcome, sizeBytes ?? 0);
    }

    private static string FormatTag(ReportFormat format) => ReportFormats.Extension(format);
}
