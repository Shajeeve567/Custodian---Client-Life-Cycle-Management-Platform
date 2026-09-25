namespace Custodian.Workflow.Configuration;
public sealed class SlaOptions
{
    public const string SectionName = "Sla";

    public int DefaultOverdueHours { get; set; } = 72;

    public Dictionary<int, int> StageOverdueHours { get; set; } = new();

    public TimeSpan RevolveFor(int stageNumber) =>
        TimeSpan.FromHours(StageOverdueHours.TryGetValue(stageNumber, out var hours) ? hours : DefaultOverdueHours);
}