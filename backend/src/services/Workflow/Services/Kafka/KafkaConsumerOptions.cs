namespace Custodian.Workflow.Services.Kafka;

/// <summary>
/// Consumer-side Kafka config for Workflow (19-N5). Binds from the same "Kafka" section as
/// <see cref="KafkaProducerOptions"/> so broker and SASL settings are configured once; the
/// consumer-only keys (ConsumerEnabled, GroupId, AutoOffsetReset) are ignored by the producer.
/// </summary>
public sealed class KafkaConsumerOptions
{
    public const string SectionName = "Kafka";

    /// <summary>Off by default: Documents does not publish to Kafka yet (see DocumentEventsConsumer).</summary>
    public bool ConsumerEnabled { get; set; } = false;
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "custodian.events";
    public string GroupId { get; set; } = "custodian-workflow";
    public string AutoOffsetReset { get; set; } = "Earliest";
    public string? SecurityProtocol { get; set; }
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
}
