using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Custodian.Workflow.Tests.Unit;

/// <summary>Shared setup for controller unit tests: a signed-in user and the anonymous { message } bodies.</summary>
internal static class ControllerTestHelpers
{
    public static ControllerContext ContextFor(params Claim[] claims) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
    };

    /// <summary>Reads <c>message</c> from a result body built as <c>new { message = ... }</c>.</summary>
    public static string? Message(IActionResult? result)
    {
        var body = Assert.IsAssignableFrom<ObjectResult>(result).Value;
        Assert.NotNull(body);
        return body.GetType().GetProperty("message")?.GetValue(body) as string;
    }
}
