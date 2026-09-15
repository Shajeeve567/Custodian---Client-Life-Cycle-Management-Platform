namespace Custodian.Audit.Services.Kafka;

/// <summary>
/// Mirrors Identity's Services/Kafka/KafkaOptions.cs shape exactly, so both
/// services bind from the same "Kafka" config section convention.
/// </summary>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public bool Enabled { get; set; } = false;
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string GroupId { get; set; } = "custodian-audit";
    public string Topic { get; set; } = "custodian.events";
    public string AutoOffsetReset { get; set; } = "Earliest";
}
