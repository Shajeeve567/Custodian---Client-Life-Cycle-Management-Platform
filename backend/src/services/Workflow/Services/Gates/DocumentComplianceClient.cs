using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Microsoft.AspNetCore.Http;

namespace Custodian.Workflow.Services.Gates;

public class DocumentComplianceClient : IDocumentComplianceClient
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<DocumentComplianceClient> _logger;

    public DocumentComplianceClient(HttpClient httpClient, IHttpContextAccessor httpContextAccessor, ILogger<DocumentComplianceClient> logger)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DocumentSummaryDto>> GetDocumentsAsync(Guid engagementId, string tenantId, CancellationToken ct = default)
    {
        var url = $"/api/engagements/{engagementId}/documents?tenantId={Uri.EscapeDataString(tenantId)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // The Documents service's own [Authorize] check requires a bearer token; there's no
        // separate service-to-service auth mechanism yet, so forward the original caller's
        // token through. Without this, every gate check would 401 against Documents.
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
            _logger.LogError(ex, "Failed to reach Documents service for engagement {EngagementId}", engagementId);
            throw new DocumentComplianceUnavailableException($"Documents service is unreachable for engagement '{engagementId}'.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Documents service returned {StatusCode} for engagement {EngagementId}", response.StatusCode, engagementId);
            throw new DocumentComplianceUnavailableException($"Documents service returned {(int)response.StatusCode} for engagement '{engagementId}'.");
        }

        var documents = await response.Content.ReadFromJsonAsync<List<DocumentSummaryDto>>(cancellationToken: ct);
        return documents ?? new List<DocumentSummaryDto>();
    }
}
