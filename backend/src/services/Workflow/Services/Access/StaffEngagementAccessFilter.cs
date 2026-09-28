using Custodian.Shared.Tenancy;
using Custodian.Workflow.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Services.Access;

/// <summary>
/// Applies <see cref="EngagementAccess"/> to every Workflow endpoint that names an engagement in its
/// route (<c>{engagementId}</c>, or <c>{id}</c> on EngagementsController): a Staff member who is not the
/// engagement's responsible staff gets 404, exactly as if it did not exist, before the action runs.
/// One global filter instead of a check per endpoint, so a new engagement endpoint is covered by default.
/// Owners and Clients pass through (clients are checked by the client ownership rules).
/// </summary>
public sealed class StaffEngagementAccessFilter : IAsyncActionFilter
{
    public const string NotFoundMessage = "Engagement not found.";

    private readonly WorkflowDbContext _db;

    public StaffEngagementAccessFilter(WorkflowDbContext db)
    {
        _db = db;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (!EngagementAccess.IsAssignmentRestricted(user) || EngagementIdFromRoute(context) is not { } engagementId)
        {
            await next();
            return;
        }

        var tenantId = user.FindFirst(TenantContext.ClaimName)?.Value?.Trim();
        var staffId = await _db.Engagements
            .AsNoTracking()
            .Where(e => e.EngagementId == engagementId && e.TenantId == tenantId)
            .Select(e => e.StaffId)
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        // A missing engagement is left to the action (its own 404); an unassigned one is hidden the same way.
        if (staffId is not null && !EngagementAccess.CanAccess(user, staffId))
        {
            context.Result = new NotFoundObjectResult(new { message = NotFoundMessage });
            return;
        }

        await next();
    }

    private static Guid? EngagementIdFromRoute(ActionExecutingContext context)
    {
        var values = context.RouteData.Values;
        var raw = values.TryGetValue("engagementId", out var engagementId)
            ? engagementId
            : context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "Engagements" } && values.TryGetValue("id", out var id)
                ? id
                : null;

        return Guid.TryParse(raw?.ToString(), out var parsed) ? parsed : null;
    }
}
