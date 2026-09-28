using System.Security.Claims;
using Custodian.Workflow.Models;

namespace Custodian.Workflow.Services.Access;

/// <summary>
/// Who may work on an engagement inside a workspace:
/// - Owner: every engagement in the workspace.
/// - Staff: only engagements where they are the responsible staff member (<see cref="Engagement.StaffId"/>).
/// - Client: only their own engagements (checked separately, by client_id, in each controller).
/// The workspace itself is enforced before this, by the tenant_id claim.
/// </summary>
public static class EngagementAccess
{
    /// <summary>Never equal to a real StaffId: used when a Staff token has no user id, so it matches nothing.</summary>
    private const string NoStaffId = "\0no-staff-id";

    public static bool IsOwner(ClaimsPrincipal? user) => user?.IsInRole("Owner") == true;

    /// <summary>A Staff member who is not also an Owner, i.e. limited to their assigned engagements.</summary>
    public static bool IsAssignmentRestricted(ClaimsPrincipal? user) =>
        user?.IsInRole("Staff") == true && !IsOwner(user);

    public static string? CallerUserId(ClaimsPrincipal? user) =>
        user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;

    /// <summary>
    /// The StaffId a Staff caller's queries must be limited to, or null when the caller is not limited
    /// (Owner) or not staff at all (Client: handled by the client ownership checks).
    /// </summary>
    public static string? RestrictedStaffId(ClaimsPrincipal? user)
    {
        if (!IsAssignmentRestricted(user)) return null;
        var id = CallerUserId(user);
        return string.IsNullOrWhiteSpace(id) ? NoStaffId : id.Trim();
    }

    /// <summary>Whether a Staff/Owner caller may open an engagement with this responsible staff member.</summary>
    public static bool CanAccess(ClaimsPrincipal? user, string? engagementStaffId)
    {
        var restricted = RestrictedStaffId(user);
        return restricted is null || string.Equals(engagementStaffId?.Trim(), restricted, StringComparison.OrdinalIgnoreCase);
    }
}
