using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Custodian.Workflow.Services.Gates;

/// <summary>
/// 19-N7: bounded resilience for the Workflow → Documents HTTP call. Each attempt is capped at
/// <see cref="AttemptTimeout"/> and a transient failure (network error, timeout, 5xx/408/429) is
/// retried at most once. Anything still failing surfaces as DocumentComplianceUnavailableException,
/// so gates and the next-action engine keep failing closed.
/// </summary>
public static class DocumentComplianceResilience
{
    public const string PipelineName = "documents-compliance";
    public const int MaxRetryAttempts = 1;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static IHttpClientBuilder AddDocumentComplianceResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler(PipelineName, pipeline =>
        {
            // Retry is the outer strategy so the timeout applies to each attempt, not the total.
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                Delay = RetryDelay,
                BackoffType = DelayBackoffType.Constant,
                UseJitter = false
            });
            pipeline.AddTimeout(AttemptTimeout);
        });

        return builder;
    }
}
