using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Custodian.System.Tests;

public static class QaApiHelper
{
    private static readonly HttpClient Http = new();
    public const string IdentityUrl = "http://localhost:5281";
    public const string WorkflowUrl = "http://localhost:5225";
    public const string AuditUrl = "http://localhost:5051";

    public record WorkspaceResult(string Token, string TenantId, string TenantName, string Email, string Password, string UserId);
    public record ClientResult(string ClientId, string Email, string Password, string Name);
    public record StaffResult(string StaffId, string Email, string Password);
    public record ActionItem(string ActionId, string Title, int StageNumber, string Status, string AssignedToRole, bool IsInternalOnly, DateTime? DeadlineUtc);

    public static async Task<WorkspaceResult> RegisterAndSetupWorkspaceAsync(string prefix = "SEL-S3")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var email = $"qa.{prefix.ToLower()}.owner.{ts}@test.com";
        var password = "QaPassword!2026Secure";
        var firmName = $"{prefix} Advisory {ts}";

        // 1. Register global account
        var regPayload = JsonSerializer.Serialize(new { email, password });
        var regRes = await Http.PostAsync($"{IdentityUrl}/api/auth/register", new StringContent(regPayload, Encoding.UTF8, "application/json"));
        regRes.EnsureSuccessStatusCode();

        // 2. Login (Global token)
        var loginRes = await Http.PostAsync($"{IdentityUrl}/api/auth/login", new StringContent(regPayload, Encoding.UTF8, "application/json"));
        loginRes.EnsureSuccessStatusCode();
        var gDoc = JsonDocument.Parse(await loginRes.Content.ReadAsStringAsync());
        var gToken = gDoc.RootElement.GetProperty("token").GetString()!;

        // 3. Create Tenant
        var req = new HttpRequestMessage(HttpMethod.Post, $"{IdentityUrl}/api/Tenant")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { name = firmName }), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gToken);
        var tenantRes = await Http.SendAsync(req);
        tenantRes.EnsureSuccessStatusCode();
        var tDoc = JsonDocument.Parse(await tenantRes.Content.ReadAsStringAsync());
        var tenantId = tDoc.RootElement.GetProperty("id").GetString()!;
        var tenantName = tDoc.RootElement.GetProperty("name").GetString()!;

        // 4. Select Workspace
        var selReq = new HttpRequestMessage(HttpMethod.Post, $"{IdentityUrl}/api/auth/select-workspace/{tenantId}");
        selReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gToken);
        var selRes = await Http.SendAsync(selReq);
        selRes.EnsureSuccessStatusCode();
        var sDoc = JsonDocument.Parse(await selRes.Content.ReadAsStringAsync());
        var wsToken = sDoc.RootElement.GetProperty("token").GetString()!;

        // Parse sub from JWT payload
        string userId = "owner-user";
        try
        {
            var parts = wsToken.Split('.');
            if (parts.Length > 1)
            {
                var payloadStr = parts[1];
                payloadStr = payloadStr.Replace('-', '+').Replace('_', '/');
                switch (payloadStr.Length % 4)
                {
                    case 2: payloadStr += "=="; break;
                    case 3: payloadStr += "="; break;
                }
                var bytes = Convert.FromBase64String(payloadStr);
                var jDoc = JsonDocument.Parse(bytes);
                if (jDoc.RootElement.TryGetProperty("sub", out var subProp))
                {
                    userId = subProp.GetString() ?? userId;
                }
            }
        }
        catch { }

        return new WorkspaceResult(wsToken, tenantId, tenantName, email, password, userId);
    }

    public static async Task<ClientResult> CreateClientWithPortalUserAsync(string wsToken, string tenantId, string prefix = "SEL-S3")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var email = $"client.{prefix.ToLower()}.{ts}@test.com";
        var password = "ClientPortal!2026";
        var name = $"Client {prefix} {ts}";

        var payload = JsonSerializer.Serialize(new
        {
            name,
            email,
            phone = "+15551234567",
            password
        });

        var req = new HttpRequestMessage(HttpMethod.Post, $"{IdentityUrl}/api/Client")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var clientId = doc.RootElement.GetProperty("id").GetString()!;

        return new ClientResult(clientId, email, password, name);
    }

    public static async Task<string> CreateEngagementAsync(string wsToken, string tenantId, string clientId, string staffId)
    {
        if (string.IsNullOrWhiteSpace(staffId))
        {
            throw new ArgumentException("staffId is required to create an engagement in the tenant.", nameof(staffId));
        }

        var payload = JsonSerializer.Serialize(new
        {
            tenantId,
            clientId,
            staffId
        });

        var req = new HttpRequestMessage(HttpMethod.Post, $"{WorkflowUrl}/api/engagements")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            throw new HttpRequestException($"CreateEngagement failed with status {res.StatusCode}: {err}");
        }

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("engagementId").GetString()!;
    }

    public static async Task StartEngagementAsync(string wsToken, string tenantId, string engagementId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            tenantId,
            status = "Started"
        });

        var req = new HttpRequestMessage(HttpMethod.Put, $"{WorkflowUrl}/api/engagements/{engagementId}/status")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public static async Task ApplyStandardChecklistAsync(string wsToken, string tenantId, string engagementId)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{WorkflowUrl}/api/engagements/{engagementId}/actions/standard-checklist?tenantId={tenantId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public static async Task<List<ActionItem>> GetActionsAsync(string wsToken, string tenantId, string engagementId)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{WorkflowUrl}/api/engagements/{engagementId}/actions?tenantId={tenantId}&isClientView=false");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var list = new List<ActionItem>();
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.GetProperty("actionId").GetString()!;
            var title = el.GetProperty("title").GetString()!;
            var stage = el.GetProperty("stageNumber").GetInt32();
            var status = el.GetProperty("status").GetString()!;
            var role = el.TryGetProperty("assignedToRole", out var rp) && rp.ValueKind == JsonValueKind.String ? rp.GetString()! : string.Empty;
            var isInternal = el.TryGetProperty("isInternalOnly", out var ii) && ii.GetBoolean();
            DateTime? dl = null;
            if (el.TryGetProperty("deadlineUtc", out var dlp) && dlp.ValueKind == JsonValueKind.String)
            {
                dl = dlp.GetDateTime();
            }
            list.Add(new ActionItem(id, title, stage, status, role, isInternal, dl));
        }
        return list;
    }

    public static async Task MakeActionOverdueAsync(string wsToken, string tenantId, string engagementId, string actionId, int daysAgo = 2)
    {
        var pastDeadline = DateTime.UtcNow.AddDays(-daysAgo).ToString("O");
        var payload = JsonSerializer.Serialize(new
        {
            deadlineUtc = pastDeadline
        });

        var req = new HttpRequestMessage(HttpMethod.Patch, $"{WorkflowUrl}/api/engagements/{engagementId}/actions/{actionId}?tenantId={tenantId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public static async Task CompleteActionAsync(string wsToken, string tenantId, string engagementId, string actionId, string completedByActor = "qa-staff")
    {
        var payload = JsonSerializer.Serialize(new
        {
            completedByActor
        });

        var req = new HttpRequestMessage(HttpMethod.Put, $"{WorkflowUrl}/api/engagements/{engagementId}/actions/{actionId}/complete?tenantId={tenantId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            throw new HttpRequestException($"CompleteAction failed with status {res.StatusCode}: {err}");
        }
    }

    public static async Task<string> AttachConditionAsync(string wsToken, string tenantId, string engagementId, string title, string type = "Approval")
    {
        var payload = JsonSerializer.Serialize(new
        {
            title,
            type,
            requiredBeforeStage = 3,
            description = "Test Condition blocker"
        });

        var req = new HttpRequestMessage(HttpMethod.Post, $"{WorkflowUrl}/api/engagements/{engagementId}/conditions?tenantId={tenantId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync();
            throw new HttpRequestException($"AttachCondition failed with status {res.StatusCode}: {err}");
        }
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("conditionId").GetString()!;
    }

    public static async Task DeactivateConditionAsync(string wsToken, string tenantId, string engagementId, string conditionId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            reason = "Selenium QA deactivation"
        });
        var req = new HttpRequestMessage(HttpMethod.Put, $"{WorkflowUrl}/api/engagements/{engagementId}/conditions/{conditionId}/deactivate?tenantId={tenantId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public static async Task<WorkspaceResult> CreateStaffUserAndLoginAsync(string ownerWsToken, string tenantId, string tenantName, string prefix = "Staff")
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var email = $"staff.{prefix.ToLower()}.{ts}@test.com";
        var password = "StaffPassword!2026";

        var payload = JsonSerializer.Serialize(new
        {
            email,
            password,
            role = 1 // Staff
        });

        var req = new HttpRequestMessage(HttpMethod.Post, $"{IdentityUrl}/api/UserAccount/invite")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ownerWsToken);
        var res = await Http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var staffId = doc.RootElement.GetProperty("id").GetString()!;

        // Login as staff
        var loginPayload = JsonSerializer.Serialize(new { email, password });
        var loginRes = await Http.PostAsync($"{IdentityUrl}/api/auth/login", new StringContent(loginPayload, Encoding.UTF8, "application/json"));
        loginRes.EnsureSuccessStatusCode();
        var gDoc = JsonDocument.Parse(await loginRes.Content.ReadAsStringAsync());
        var gToken = gDoc.RootElement.GetProperty("token").GetString()!;

        // Select workspace
        var selReq = new HttpRequestMessage(HttpMethod.Post, $"{IdentityUrl}/api/auth/select-workspace/{tenantId}");
        selReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gToken);
        var selRes = await Http.SendAsync(selReq);
        selRes.EnsureSuccessStatusCode();
        var sDoc = JsonDocument.Parse(await selRes.Content.ReadAsStringAsync());
        var staffWsToken = sDoc.RootElement.GetProperty("token").GetString()!;

        return new WorkspaceResult(staffWsToken, tenantId, tenantName, email, password, staffId);
    }
}
