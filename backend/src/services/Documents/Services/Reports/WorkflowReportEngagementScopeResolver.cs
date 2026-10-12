using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Custodian.Shared.Auth;
using Custodian.Shared.Reporting.Auth;
using Custodian.Shared.Reporting.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Custodian.Documents.Services.Reports;

/// <summary>
/// CSTD-182: Workflow-backed implementation of <see cref="IReportEngagementScopeResolver"/>.
/// Resolves Staff-assigned engagement boundaries by querying Workflow's caller-filtered GET /api/engagements.
/// Enforces fail-closed semantics: any network failure or non-success status fails closed as a
/// 503 ProblemDetails via <see cref="ReportGenerationException.DataSourceUnavailable"/>.
/// </summary>
public sealed class WorkflowReportEngagementScopeResolver : IReportEngagementScopeResolver
{
    private readonly HttpClient _httpClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<WorkflowReportEngagementScopeResolver> _logger;

    public WorkflowReportEngagementScopeResolver(
        HttpClient httpClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<WorkflowReportEngagementScopeResolver> logger)
    {
        _httpClient = httpClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<Guid>?> ResolveAllowedEngagementIdsAsync(
        ClaimsPrincipal user,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // Security boundary: Must be Owner or Staff
        ReportAuthorization.EnsureStaffOrOwner(user);

        // Owner: Unrestricted across authenticated tenant workspace
        if (user.IsInRole(nameof(Role.Owner)))
        {
            return null;
        }

        // Staff: Must query Workflow as caller to determine assigned engagements
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/engagements?tenantId={Uri.EscapeDataString(tenantId.Trim())}");

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
            _logger.LogWarning(ex, "Workflow service unreachable while resolving staff engagement scope for tenant {TenantId}", tenantId);
            throw ReportGenerationException.DataSourceUnavailable("The Workflow service is unreachable right now.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Workflow returned status {StatusCode} while resolving staff engagement scope for tenant {TenantId}",
                    response.StatusCode, tenantId);
                throw ReportGenerationException.DataSourceUnavailable("The Workflow service is unreachable right now.");
            }

            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var jsonDoc = await JsonDocument.ParseAsync(body, cancellationToken: ct);

                if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Workflow returned unexpected non-array response for engagements");
                    throw ReportGenerationException.DataSourceUnavailable("The Workflow service returned an invalid response.");
                }

                var allowedIds = new List<Guid>();
                foreach (var element in jsonDoc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("engagementId", out var idProp) ||
                        element.TryGetProperty("EngagementId", out idProp))
                    {
                        if (idProp.TryGetGuid(out var parsedId) ||
                            (idProp.ValueKind == JsonValueKind.String && Guid.TryParse(idProp.GetString(), out parsedId)))
                        {
                            allowedIds.Add(parsedId);
                        }
                    }
                }

                return allowedIds;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse engagement JSON response from Workflow");
                throw ReportGenerationException.DataSourceUnavailable("The Workflow service returned an invalid response.", ex);
            }
        }
    }
}
