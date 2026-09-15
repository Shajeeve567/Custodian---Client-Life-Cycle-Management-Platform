using System.Net;
using System.Text;
using System.Text.Json;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Gates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Unit tests for DocumentComplianceClient, using a fake HttpMessageHandler rather than a
/// mocking library extension — avoids adding a new package dependency for one test file.
/// </summary>
public class DocumentComplianceClientTests
{
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string? _jsonBody;
        public HttpRequestMessage? LastRequest { get; private set; }

        public FakeHttpMessageHandler(HttpStatusCode statusCode, string? jsonBody)
        {
            _statusCode = statusCode;
            _jsonBody = jsonBody;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_statusCode);
            if (_jsonBody != null)
            {
                response.Content = new StringContent(_jsonBody, Encoding.UTF8, "application/json");
            }
            return Task.FromResult(response);
        }
    }

    private static DocumentComplianceClient CreateClient(HttpStatusCode statusCode, string? jsonBody, string? bearerToken = "Bearer test-token")
    {
        var handler = new FakeHttpMessageHandler(statusCode, jsonBody);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5171") };

        var httpContext = new DefaultHttpContext();
        if (bearerToken != null)
        {
            httpContext.Request.Headers.Authorization = bearerToken;
        }

        var contextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        return new DocumentComplianceClient(httpClient, contextAccessor, NullLogger<DocumentComplianceClient>.Instance);
    }

    [Fact]
    public async Task GetDocumentsAsync_SuccessResponse_DeserializesDocuments()
    {
        // Arrange
        var documents = new List<DocumentSummaryDto>
        {
            new() { DocumentId = Guid.NewGuid(), Type = "GovernmentId", ComplianceStatus = "Compliant", VerificationStatus = "Verified" }
        };
        var json = JsonSerializer.Serialize(documents, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var client = CreateClient(HttpStatusCode.OK, json);

        // Act
        var result = await client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001");

        // Assert
        Assert.Single(result);
        Assert.Equal("GovernmentId", result[0].Type);
    }

    [Fact]
    public async Task GetDocumentsAsync_ForwardsIncomingAuthorizationHeader()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "[]");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5171") };
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer caller-token";
        var contextAccessor = new HttpContextAccessor { HttpContext = httpContext };
        var client = new DocumentComplianceClient(httpClient, contextAccessor, NullLogger<DocumentComplianceClient>.Instance);

        // Act
        await client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001");

        // Assert: the Documents service's own [Authorize] check needs this forwarded,
        // since there's no separate service-to-service auth mechanism.
        Assert.Equal("Bearer caller-token", handler.LastRequest?.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetDocumentsAsync_NonSuccessStatusCode_ThrowsDocumentComplianceUnavailable()
    {
        // Arrange
        var client = CreateClient(HttpStatusCode.InternalServerError, null);

        // Act & Assert: must fail closed with a typed exception, not swallow the error
        await Assert.ThrowsAsync<DocumentComplianceUnavailableException>(() =>
            client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001"));
    }

    [Fact]
    public async Task GetDocumentsAsync_EmptyArrayResponse_ReturnsEmptyList()
    {
        // Arrange
        var client = CreateClient(HttpStatusCode.OK, "[]");

        // Act
        var result = await client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001");

        // Assert
        Assert.Empty(result);
    }
}
