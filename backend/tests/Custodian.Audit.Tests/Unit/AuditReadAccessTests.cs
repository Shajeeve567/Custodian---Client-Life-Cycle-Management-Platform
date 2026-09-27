using System.Reflection;
using Custodian.Audit.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// The audit log covers every engagement (and client) in the workspace, so reading or verifying it is
/// Owner/Staff only. Previously any workspace member, including a Client, could read all of it.
/// </summary>
public class AuditReadAccessTests
{
    public static IEnumerable<object[]> ReadEndpoints() =>
        typeof(AuditEventsController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any(a => a.HttpMethods.Contains("GET")))
            .Select(m => new object[] { m.Name });

    [Theory]
    [MemberData(nameof(ReadEndpoints))]
    public void EveryReadEndpoint_IsOwnerOrStaffOnly(string methodName)
    {
        var method = typeof(AuditEventsController).GetMethod(methodName)!;
        var roles = method.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Roles).SingleOrDefault(r => r != null);

        Assert.Equal("Owner,Staff", roles);
    }

    [Fact]
    public void ThereAreReadEndpointsToCheck()
    {
        Assert.True(ReadEndpoints().Count() >= 4);
    }
}
