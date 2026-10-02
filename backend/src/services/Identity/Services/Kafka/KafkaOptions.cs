namespace Custodian.Identity.Services.Kafka;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public bool Enabled { get; set; } = false;
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string GroupId { get; set; } = "custodian-identity-notifications-group";
    public string Topic { get; set; } = "custodian.events";
    public string AutoOffsetReset { get; set; } = "Earliest";
    public string? SecurityProtocol { get; set; }
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
}
