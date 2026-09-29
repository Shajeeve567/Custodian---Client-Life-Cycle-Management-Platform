namespace Custodian.Workflow.Configuration;
public sealed class SlaOptions
{
    public const string SectionName = "Sla";

    public int DefaultOverdueHours { get; set; } = 72;

    public Dictionary<int, int> StageOverdueHours { get; set; } = new();

    /// <summary>CSTD-34-2 stall queue urgency weights (Sla:Urgency).</summary>
    public SlaUrgencyOptions Urgency { get; set; } = new();

    public TimeSpan RevolveFor(int stageNumber) =>
        TimeSpan.FromHours(StageOverdueHours.TryGetValue(stageNumber, out var hours) ? hours : DefaultOverdueHours);
}

public sealed class SlaUrgencyOptions
{
    /// <summary>Weight for a blocker that gates the next stage (requirement, evidence, linked task).</summary>
    public double GatingWeight { get; set; } = 2.0;

    /// <summary>Weight for any other blocker.</summary>
    public double DefaultWeight { get; set; } = 1.0;
}
