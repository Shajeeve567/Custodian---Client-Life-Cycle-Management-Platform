using System.Text;
using System.Text.Json;

namespace Custodian.Workflow.Services;

public class AuditPublisher : IAuditPublisher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuditPublisher> _logger;

    public AuditPublisher(HttpClient httpClient, ILogger<AuditPublisher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task PublishEventAsync(Guid engagementId, string tenantId, string actor, string type, object payload)
    {
        try
        {
            var payloadJson = JsonSerializer.Serialize(payload);

            var requestBody = new
            {
                engagementId,
                tenantId = Guid.TryParse(tenantId, out var parsedTenant) ? parsedTenant : (Guid?)null,
                actor,
                type,
                payload = payloadJson
            };

            var content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("/api/audit-events", content);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to publish audit event '{Type}' for engagement '{EngagementId}'. Status code: {StatusCode}",
                    type, engagementId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Per business rules: audit event side effects must not silently corrupt/fail the primary business transaction
            _logger.LogError(ex, "Error publishing audit event '{Type}' for engagement '{EngagementId}'", type, engagementId);
        }
    }
}
