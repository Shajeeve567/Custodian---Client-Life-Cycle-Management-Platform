namespace Custodian.Audit.Tests.Unit.EngagementAccess;

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Custodian.Audit.Services.EngagementAccess;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

public class WorkflowEngagementAccessClientTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _engagementId = Guid.NewGuid();

    private HttpClient CreateMockHttpClient(HttpResponseMessage response)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(response);

        return new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:5225")
        };
    }

    private HttpClient CreateThrowingHttpClient(Exception exception)
    {
        var handlerMock = new Mock<HttpMessageHandler>();
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(exception);

        return new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("http://localhost:5225")
        };
    }

    [Fact]
    public async Task GetEngagementSummaryAsync_WorkflowReturns200_DeserializesEngagementSummary()
    {
        // Arrange
        var json = JsonSerializer.Serialize(new
        {
            engagementId = _engagementId,
            tenantId = _tenantId.ToString(),
            clientId = "client-guid-1",
            staffId = "staff-guid-1",
            status = "Draft",
            stage = "Onboarding",
            stageProgressPercentage = 20,
            createdAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            closedAt = (DateTime?)null
        });

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        var httpClient = CreateMockHttpClient(httpResponse);
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        // Act
        var result = await client.GetEngagementSummaryAsync(_engagementId, _tenantId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(_engagementId, result!.EngagementId);
        Assert.Equal(_tenantId.ToString(), result.TenantId);
        Assert.Equal("client-guid-1", result.ClientId);
        Assert.Equal("staff-guid-1", result.StaffId);
        Assert.Equal("Draft", result.Status);
        Assert.Equal("Onboarding", result.Stage);
        Assert.Equal(20, result.StageProgressPercentage);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), result.CreatedAt);
        Assert.Null(result.ClosedAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task GetEngagementSummaryAsync_WorkflowReturns404Or403Or401_ReturnsNull(HttpStatusCode statusCode)
    {
        // Arrange
        var httpResponse = new HttpResponseMessage(statusCode);
        var httpClient = CreateMockHttpClient(httpResponse);
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        // Act
        var result = await client.GetEngagementSummaryAsync(_engagementId, _tenantId);

        // Assert: Access denied or engagement missing returns null (so callers fail closed without leakage)
        Assert.Null(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetEngagementSummaryAsync_WorkflowReturns5xx_ThrowsEngagementAccessUnavailableException(HttpStatusCode statusCode)
    {
        // Arrange
        var httpResponse = new HttpResponseMessage(statusCode);
        var httpClient = CreateMockHttpClient(httpResponse);
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() =>
            client.GetEngagementSummaryAsync(_engagementId, _tenantId));
    }

    [Fact]
    public async Task GetEngagementSummaryAsync_WorkflowUnreachable_ThrowsEngagementAccessUnavailableException()
    {
        // Arrange
        var httpClient = CreateThrowingHttpClient(new HttpRequestException("Connection refused"));
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() =>
            client.GetEngagementSummaryAsync(_engagementId, _tenantId));
    }

    [Fact]
    public async Task GetEngagementSummaryAsync_WorkflowReturnsMismatchedEngagementId_ThrowsEngagementAccessUnavailableException()
    {
        var mismatchedId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            engagementId = mismatchedId,
            tenantId = _tenantId.ToString(),
            clientId = "client-guid-1",
            staffId = "staff-guid-1",
            status = "Draft",
            stage = "Onboarding",
            stageProgressPercentage = 20,
            createdAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            closedAt = (DateTime?)null
        });

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        var httpClient = CreateMockHttpClient(httpResponse);
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() =>
            client.GetEngagementSummaryAsync(_engagementId, _tenantId));
    }

    [Fact]
    public async Task GetEngagementSummaryAsync_WorkflowReturnsMismatchedTenantId_ThrowsEngagementAccessUnavailableException()
    {
        var mismatchedTenant = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new
        {
            engagementId = _engagementId,
            tenantId = mismatchedTenant.ToString(),
            clientId = "client-guid-1",
            staffId = "staff-guid-1",
            status = "Draft",
            stage = "Onboarding",
            stageProgressPercentage = 20,
            createdAt = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
            closedAt = (DateTime?)null
        });

        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        var httpClient = CreateMockHttpClient(httpResponse);
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() =>
            client.GetEngagementSummaryAsync(_engagementId, _tenantId));
    }

    [Fact]
    public async Task GetEngagementSummaryAsync_WorkflowReturnsEmptyPayload_ThrowsEngagementAccessUnavailableException()
    {
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
        };
        var httpClient = CreateMockHttpClient(httpResponse);
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();

        var client = new WorkflowEngagementAccessClient(
            httpClient,
            httpContextAccessorMock.Object,
            NullLogger<WorkflowEngagementAccessClient>.Instance);

        await Assert.ThrowsAsync<EngagementAccessUnavailableException>(() =>
            client.GetEngagementSummaryAsync(_engagementId, _tenantId));
    }
}
