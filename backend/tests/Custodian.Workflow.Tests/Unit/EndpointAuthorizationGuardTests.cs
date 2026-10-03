using System.Reflection;
using Custodian.Workflow.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>
/// Regression guard for the security hardening: every state-changing Workflow endpoint must be
/// restricted to Owner/Staff by attribute, unless it is one of the few deliberate client actions
/// below (each of those enforces client ownership inside the action). A new write endpoint without
/// a role fails this test instead of silently shipping open to Clients.
/// </summary>
public class EndpointAuthorizationGuardTests
{
    private static readonly HashSet<string> ClientWritableEndpoints = new()
    {
        "ClientActionsController.CompleteAction",       // own plain tasks only (GetClientCompletionBlockReasonAsync)
        "ClientActionsController.UploadEvidence",       // outcome read from Documents, never the caller
        "RequirementsController.SubmitRequirement",     // client submits requested information
        "EngagementConditionsController.ApproveCondition", // CSTD-143: client approves condition
        "EngagementConditionsController.RejectCondition"   // CSTD-143: client rejects condition
    };

    public static IEnumerable<object[]> WriteEndpoints() =>
        typeof(EngagementsController).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a =>
                    a.HttpMethods.Any(v => v is "POST" or "PUT" or "PATCH" or "DELETE")))
                .Select(m => new object[] { $"{t.Name}.{m.Name}" }));

    private static MethodInfo Resolve(string endpoint)
    {
        var parts = endpoint.Split('.');
        var type = typeof(EngagementsController).Assembly.GetTypes().Single(t => t.Name == parts[0]);
        return type.GetMethod(parts[1], BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!;
    }

    [Theory]
    [MemberData(nameof(WriteEndpoints))]
    public void WriteEndpoint_IsStaffOnly_OrAnExplicitClientAction(string endpoint)
    {
        if (ClientWritableEndpoints.Contains(endpoint))
        {
            return;
        }

        var method = Resolve(endpoint);
        var roles = method.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(method.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>())
            .Select(a => a.Roles)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .ToList();

        Assert.True(roles.Count > 0, $"{endpoint} changes state but has no [Authorize(Roles = ...)].");
        Assert.All(roles, r => Assert.DoesNotContain("Client", r!));
        Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Guard_FindsTheWorkflowWriteEndpoints()
    {
        Assert.True(WriteEndpoints().Count() >= 15);
    }
}
