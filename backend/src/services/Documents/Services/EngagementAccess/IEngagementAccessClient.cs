namespace Custodian.Documents.Services.EngagementAccess;

/// <summary>
/// Asks the Workflow service whether the current caller may access an engagement. Engagement
/// ownership lives in Workflow, so Documents does not duplicate it: Workflow's
/// GET /api/engagements/{id} returns 404 to a Client that does not own the engagement.
/// </summary>
public interface IEngagementAccessClient
{
    /// <returns>True when the caller may access the engagement; false when Workflow denies it.</returns>
    /// <exception cref="EngagementAccessUnavailableException">Workflow could not be reached or answered unexpectedly.</exception>
    Task<bool> CanAccessEngagementAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}

public sealed class EngagementAccessUnavailableException : Exception
{
    public EngagementAccessUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
