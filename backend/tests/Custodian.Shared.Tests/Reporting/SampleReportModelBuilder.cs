using System.Globalization;
using Custodian.Shared.Reporting.Models;

namespace Custodian.Shared.Tests.Reporting;

/// <summary>
/// CSTD-36-4 test-only report (no production endpoint): proves a new report needs only a data
/// shape and a model builder, with rendering, CSV, file naming and errors reused from
/// Custodian.Shared.Reporting (AC1). Every number comes from the input rows (AC2).
/// </summary>
public static class SampleReportModelBuilder
{
    public const string ReportCode = "SAMPLE_TASKS";

    public sealed record SampleTask(string Stage, bool Completed, double HoursOpen);

    public sealed class SampleReport(ReportMetadata metadata, IEnumerable<ReportSection> sections, TableSection? detail)
        : ReportModel(metadata, sections)
    {
        /// <summary>The per-task table the CSV export uses; null when nothing matched.</summary>
        public TableSection? Detail { get; } = detail;
    }

    public static SampleReport Build(IReadOnlyList<SampleTask> tasks, DateTimeOffset now, string stageFilter = "All")
    {
        var metadata = new ReportMetadata(
            ReportCode,
            "Sample task report",
            "tenant-sample",
            now,
            "tester@example.com",
            [new("Stage", stageFilter)],
            "Test fixture");

        var completed = tasks.Count(t => t.Completed);
        var rate = tasks.Count == 0 ? "n/a" : (completed * 100.0 / tasks.Count).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        var sections = new List<ReportSection>
        {
            new KeyValueSection("Summary",
            [
                new("Total tasks", tasks.Count),
                new("Completed", completed),
                new("Completion rate", rate)
            ])
        };

        TableSection? detail = null;
        if (tasks.Count == 0)
        {
            sections.Add(new EmptySection("By stage"));
        }
        else
        {
            sections.Add(new TableSection(
                "By stage",
                [new ReportColumn("Stage"), new ReportColumn("Tasks", ReportColumnAlignment.Right), new ReportColumn("Avg hours open", ReportColumnAlignment.Right)],
                tasks.GroupBy(t => t.Stage)
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => new object?[] { g.Key, g.Count(), Math.Round(g.Average(t => t.HoursOpen), 1) })));

            detail = new TableSection(
                "Tasks",
                [new ReportColumn("Stage"), new ReportColumn("Completed"), new ReportColumn("Hours open", ReportColumnAlignment.Right)],
                tasks.Select(t => new object?[] { t.Stage, t.Completed, t.HoursOpen }));
        }

        sections.Add(new TextSection("Definitions", ["Completion rate = completed tasks ÷ all tasks."]));
        return new SampleReport(metadata, sections, detail);
    }
}
