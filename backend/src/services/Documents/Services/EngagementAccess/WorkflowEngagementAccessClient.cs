using System.Net;
using System.Text.Json;

namespace Custodian.Documents.Services.EngagementAccess;

public sealed class WorkflowEngagementAccessClient : IEngagementAccessClient
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<WorkflowEngagementAccessClient> _logger;

    public WorkflowEngagementAccessClient(
        HttpClient httpClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<WorkflowEngagementAccessClient> logger)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<bool> CanAccessEngagementAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsCallerAsync(engagementId, tenantId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Workflow unreachable while checking access to engagement {EngagementId}", engagementId);
            throw new EngagementAccessUnavailableException("Workflow service is unreachable.", ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return false;
            }

            _logger.LogWarning("Workflow returned {StatusCode} while checking access to engagement {EngagementId}", response.StatusCode, engagementId);
            throw new EngagementAccessUnavailableException($"Workflow returned {(int)response.StatusCode}.");
        }
    }

    public async Task<string?> GetEngagementClientIdAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendAsCallerAsync(engagementId, tenantId, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Workflow returned {StatusCode} while reading the client of engagement {EngagementId}", response.StatusCode, engagementId);
                return null;
            }

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return json.RootElement.TryGetProperty("clientId", out var clientId) && clientId.ValueKind == JsonValueKind.String
                ? clientId.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read the client of engagement {EngagementId} from Workflow", engagementId);
            return null;
        }
    }

    private Task<HttpResponseMessage> SendAsCallerAsync(Guid engagementId, string tenantId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/engagements/{engagementId}?tenantId={Uri.EscapeDataString(tenantId)}");

        // Ask as the caller: Workflow applies its own tenant and client-ownership rules to this token.
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(incomingAuth))
        {
            request.Headers.TryAddWithoutValidation("Authorization", incomingAuth);
        }

        return _httpClient.SendAsync(request, ct);
    }
}
