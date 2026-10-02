using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Custodian.Workflow.DTOs;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

public class MeetingsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly bool DbReachable = ProbeMySql();

    public MeetingsEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [SkippableFact]
    public async Task Create_Unauthenticated_Returns401()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/meetings",
            new CreateMeetingRequest
            {
                Type = "Normal",
                Purpose = "x",
                ScheduledAtUtc = DateTime.UtcNow.AddDays(1),
            });

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Create_MismatchedTenant_Returns403()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");

        var tenant = Guid.NewGuid();
        var other = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));

        var resp = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/meetings?tenantId={other}",
            new CreateMeetingRequest
            {
                Type = "Normal",
                Purpose = "x",
                ScheduledAtUtc = DateTime.UtcNow.AddDays(1),
            });

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [SkippableFact]
    public async Task Create_UnknownEngagement_Returns404()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var resp = await client.PostAsJsonAsync(
            $"/api/engagements/{Guid.NewGuid()}/meetings",
            new CreateMeetingRequest
            {
                Type = "Normal",
                Purpose = "x",
                ScheduledAtUtc = DateTime.UtcNow.AddDays(1),
            });

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [SkippableFact]
    public async Task CreateAndList_RoundTripsThroughPipeline()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        // Need a real engagement in this tenant. Create one.
        var createEng = await client.PostAsJsonAsync("/api/Engagements",
            new CreateEngagementRequest
            {
                TenantId = tenant.ToString(),
                ClientId = Guid.NewGuid().ToString(),
                StaffId = Guid.NewGuid().ToString(),
            });
        createEng.EnsureSuccessStatusCode();
        var engagement = await createEng.Content.ReadFromJsonAsync<EngagementResponse>();
        Assert.NotNull(engagement);

        // Create meeting
        var create = await client.PostAsJsonAsync(
            $"/api/engagements/{engagement!.EngagementId}/meetings",
            new CreateMeetingRequest
            {
                Type = "Normal",
                Purpose = "Integration test meeting",
                ScheduledAtUtc = DateTime.UtcNow.AddDays(2),
                Importance = "Important",
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // List
        var list = await client.GetFromJsonAsync<List<MeetingResponse>>(
            $"/api/engagements/{engagement.EngagementId}/meetings");
        Assert.NotNull(list);
        Assert.Single(list!);
        Assert.Equal("Integration test meeting", list[0].Purpose);
    }

    [SkippableFact]
    public async Task Reschedule_PreservesOriginalAndLinksNew()
    {
        Skip.IfNot(DbReachable, "MySQL not reachable.");

        var tenant = Guid.NewGuid();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TestTokenFactory.CreateOwnerToken(tenant));
        client.DefaultRequestHeaders.Add("X-Tenant-Id", tenant.ToString());

        var createEng = await client.PostAsJsonAsync("/api/Engagements",
            new CreateEngagementRequest
            {
                TenantId = tenant.ToString(),
                ClientId = Guid.NewGuid().ToString(),
                StaffId = Guid.NewGuid().ToString(),
            });
        createEng.EnsureSuccessStatusCode();
        var engagement = await createEng.Content.ReadFromJsonAsync<EngagementResponse>();

        var original = await (await client.PostAsJsonAsync(
            $"/api/engagements/{engagement!.EngagementId}/meetings",
            new CreateMeetingRequest
            {
                Type = "Normal",
                Purpose = "Reschedule test",
                ScheduledAtUtc = DateTime.UtcNow.AddDays(2),
            })).Content.ReadFromJsonAsync<MeetingResponse>();

        var rescheduled = await (await client.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/meetings/{original!.MeetingId}/reschedule",
            new RescheduleMeetingRequest
            {
                NewScheduledAtUtc = DateTime.UtcNow.AddDays(4),
                Reason = "Moved",
            })).Content.ReadFromJsonAsync<MeetingResponse>();

        Assert.NotNull(rescheduled);
        Assert.Equal(original.MeetingId, rescheduled!.RescheduledFromMeetingId);

        var all = await client.GetFromJsonAsync<List<MeetingResponse>>(
            $"/api/engagements/{engagement.EngagementId}/meetings");

        Assert.NotNull(all);
        Assert.Equal(2, all!.Count);
        Assert.Contains(all, m => m.MeetingId == original.MeetingId && m.Status == "Rescheduled");
        Assert.Contains(all, m => m.MeetingId == rescheduled.MeetingId && m.Status == "Scheduled");
    }

    private static bool ProbeMySql()
    {
        try
        {
            var cs = Environment.GetEnvironmentVariable("ConnectionStrings__AzureMySqlConnection")
                     ?? Environment.GetEnvironmentVariable("ConnectionStrings__Default")
                     ?? throw new InvalidOperationException("Set ConnectionStrings__Default.");
            using var conn = new MySqlConnector.MySqlConnection(cs);
            conn.Open();
            return true;
        }
        catch { return false; }
    }
}