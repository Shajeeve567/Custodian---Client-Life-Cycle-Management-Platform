using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services;

/// <summary>
/// Supplies the engagement's ClientId and its raw ClientAction rows for stall
/// computation. Returns null when the engagement does not exist in the tenant.
/// </summary>
public interface IStallActionsProvider
{
    Task<StallActionsResult?> GetRawActionsAsync(Guid engagementId, string tenantId, CancellationToken ct = default);
}

public sealed record StallActionsResult(
    string ClientId,
    IReadOnlyList<ClientAction> Actions);