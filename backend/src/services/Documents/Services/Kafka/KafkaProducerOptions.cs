namespace Custodian.Documents.Services.Kafka;

/// <summary>Producer-side Kafka config, bound from the "Kafka" section (same keys as Workflow).</summary>
public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "custodian.events";
    public string ClientId { get; set; } = "documents-service";
    public string? SecurityProtocol { get; set; }
    public string? SaslMechanism { get; set; }
    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }
}
