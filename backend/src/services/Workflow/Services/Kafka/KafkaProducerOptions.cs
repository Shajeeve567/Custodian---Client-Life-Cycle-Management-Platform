namespace Custodian.Workflow.Services.Kafka;

/// <summary>
/// Producer-side Kafka config. Deliberately smaller than a consumer's options:
/// GroupId/AutoOffsetReset are consumer-only concepts and have no meaning here.
/// </summary>
public sealed class KafkaProducerOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";
    public string Topic { get; set; } = "custodian.events";
    public string ClientId { get; set; } = "workflow-service";
}
