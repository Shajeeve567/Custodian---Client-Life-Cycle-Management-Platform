using System.Net;
using System.Text.Json;

namespace Custodian.Audit.Services.EngagementAccess;

/// <summary>
/// Asks the Workflow service which engagements the current caller may see. Engagement assignment lives in
/// Workflow (a Staff member only reaches engagements they are the responsible staff for), so Audit does not
/// duplicate it: it calls Workflow with the caller's own token and applies the answer to the audit trail.
/// </summary>
public interface IEngagementAccessClient
{
    /// <returns>True when the caller may open the engagement; false when Workflow hides it (404/403).</returns>
    /// <exception cref="EngagementAccessUnavailableException">Workflow could not be reached or answered unexpectedly.</exception>
    Task<bool> CanAccessEngagementAsync(Guid engagementId, Guid tenantId, CancellationToken ct = default);

    /// <summary>The ids of every engagement the caller may open in the tenant.</summary>
    /// <exception cref="EngagementAccessUnavailableException">Workflow could not be reached or answered unexpectedly.</exception>
    Task<IReadOnlySet<Guid>> GetAccessibleEngagementIdsAsync(Guid tenantId, CancellationToken ct = default);
}

public sealed class EngagementAccessUnavailableException : Exception
{
    public EngagementAccessUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

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

    public async Task<bool> CanAccessEngagementAsync(Guid engagementId, Guid tenantId, CancellationToken ct = default)
    {
        using var response = await SendAsCallerAsync($"/api/Engagements/{engagementId}?tenantId={tenantId}", ct);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return false;

        _logger.LogWarning("Workflow returned {StatusCode} while checking access to engagement {EngagementId}", response.StatusCode, engagementId);
        throw new EngagementAccessUnavailableException($"Workflow returned {(int)response.StatusCode}.");
    }

    public async Task<IReadOnlySet<Guid>> GetAccessibleEngagementIdsAsync(Guid tenantId, CancellationToken ct = default)
    {
        using var response = await SendAsCallerAsync($"/api/Engagements?tenantId={tenantId}", ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Workflow returned {StatusCode} while listing the caller's engagements", response.StatusCode);
            throw new EngagementAccessUnavailableException($"Workflow returned {(int)response.StatusCode}.");
        }

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            var ids = new HashSet<Guid>();
            foreach (var engagement in json.RootElement.EnumerateArray())
            {
                if (engagement.TryGetProperty("engagementId", out var id) && id.TryGetGuid(out var guid)) ids.Add(guid);
            }
            return ids;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new EngagementAccessUnavailableException("Workflow returned an unreadable engagement list.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsCallerAsync(string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        // Ask as the caller: Workflow applies its own tenant and assignment rules to this token.
        var incomingAuth = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrWhiteSpace(incomingAuth))
        {
            request.Headers.TryAddWithoutValidation("Authorization", incomingAuth);
        }

        try
        {
            return await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Workflow unreachable while checking engagement access");
            throw new EngagementAccessUnavailableException("Workflow service is unreachable.", ex);
        }
    }
}
