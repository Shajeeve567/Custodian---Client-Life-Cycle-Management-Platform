using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Custodian.Shared.Messaging;

/// <summary>
/// Says at startup where a service's audit events go, so a local run without Kafka (or an Azure app
/// missing its Kafka settings) is obvious from the first log lines instead of from missing events.
/// </summary>
public static class AuditTransport
{
    public static void LogSelection(ILogger logger, IConfiguration configuration, string transport)
    {
        if (string.Equals(transport, "Kafka", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Audit events are published to Kafka topic {Topic} on {BootstrapServers}. If no broker is reachable, " +
                "each event fails after the producer timeout and is logged as an error (start one locally with `docker compose up -d kafka`).",
                configuration["Kafka:Topic"] ?? "custodian.events",
                configuration["Kafka:BootstrapServers"] ?? "localhost:9092");
            return;
        }

        var hasKey = AuditIngestion.IsUsableKey(configuration[AuditIngestion.ConfigKey]);
        logger.Log(
            hasKey ? LogLevel.Information : LogLevel.Warning,
            "Audit events are posted over HTTP to {AuditUrl}{KeyNote}. Other services' consumers (notifications, document sync) receive nothing on this transport.",
            configuration["Services:AuditUrl"] ?? "http://localhost:5051",
            hasKey ? string.Empty : " WITHOUT a usable AuditIngestion:ApiKey, so Audit will refuse them");
    }
}
