using System.Net;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Gates;
using Microsoft.Extensions.DependencyInjection;
using Polly.Timeout;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// CSTD-19 (19-N7): the Documents HttpClient retries a transient failure at most once and still
/// fails closed (DocumentComplianceUnavailableException) when the dependency stays down.
/// Uses the same AddDocumentComplianceResilience registration as Program.cs.
/// </summary>
public class DocumentComplianceResilienceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public int Calls { get; private set; }

        public StubHandler(params Func<HttpResponseMessage>[] responses) => _responses = new Queue<Func<HttpResponseMessage>>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var next = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return Task.FromResult(next());
        }
    }

    private static IDocumentComplianceClient CreateClient(StubHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddHttpClient<IDocumentComplianceClient, DocumentComplianceClient>(c => c.BaseAddress = new Uri("http://documents.test"))
            .AddDocumentComplianceResilience()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IDocumentComplianceClient>();
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new[] { new DocumentSummaryDto { DocumentId = Guid.NewGuid(), Type = "KYC_PASSPORT" } })
    };

    private static HttpResponseMessage ServerError() => new(HttpStatusCode.ServiceUnavailable);

    [Fact]
    public async Task TransientFailureThenSuccess_RetriesOnce_AndReturnsDocuments()
    {
        var handler = new StubHandler(ServerError, Ok);
        var client = CreateClient(handler);

        var documents = await client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001");

        Assert.Single(documents);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task PersistentFailure_MakesAtMostTwoCalls_ThenFailsClosed()
    {
        var handler = new StubHandler(ServerError);
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<DocumentComplianceUnavailableException>(() => client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001"));
        Assert.Equal(1 + DocumentComplianceResilience.MaxRetryAttempts, handler.Calls);
    }

    [Fact]
    public async Task AttemptTimeout_IsMappedToDocumentComplianceUnavailable()
    {
        // Simulates the per-attempt timeout firing on every attempt without waiting 5 seconds.
        var handler = new StubHandler(() => throw new TimeoutRejectedException());
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<DocumentComplianceUnavailableException>(() => client.GetDocumentsAsync(Guid.NewGuid(), "tenant-001"));
        Assert.Equal(2, handler.Calls);
    }
}
