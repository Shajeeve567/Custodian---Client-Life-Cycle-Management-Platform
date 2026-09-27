using System.Net;

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
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/engagements/{engagementId}?tenantId={Uri.EscapeDataString(tenantId)}");

        // Ask as the caller: Workflow applies its own tenant and client-ownership rules to this token.
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(incomingAuth))
        {
            request.Headers.TryAddWithoutValidation("Authorization", incomingAuth);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct);
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
}
