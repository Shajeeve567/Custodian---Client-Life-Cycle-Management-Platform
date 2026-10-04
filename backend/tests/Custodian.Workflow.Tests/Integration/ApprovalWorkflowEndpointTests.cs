using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Custodian.Workflow.Tests.Integration;

/// <summary>
/// CSTD-25 (CSTD-146): End-to-end service/API integration tests for the Client Approval Workflow.
/// Exercises the real ASP.NET Core HTTP pipeline, real controllers, routing, authentication/authorization,
/// model validation, and real ConditionService + ClientActionService backed by WorkflowDbContext.
/// </summary>
public class ApprovalWorkflowEndpointTests : IClassFixture<ApprovalWorkflowEndpointTests.Factory>
{
    private const string JwtKey = "custodian_super_secret_development_signing_key_at_least_64_bytes_long_1234567890";
    private const string JwtIssuer = "custodian-identity";
    private const string JwtAudience = "custodian-services";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _databaseName = $"approval-integration-{Guid.NewGuid()}";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:AzureMySqlConnection", "YOUR_SECRET_STRING");
            builder.UseSetting("ConnectionStrings:Default", "");
            builder.UseSetting("Audit:Transport", "Http");
            builder.UseSetting("Kafka:ConsumerEnabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.AddDbContext<WorkflowDbContext>(options =>
                    options.UseInMemoryDatabase(_databaseName));
            });
        }
    }

    private readonly Factory _factory;

    public ApprovalWorkflowEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthenticatedClient(string role, string userId, string tenantId, string? clientId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(JwtRegisteredClaimNames.Email, $"{role.ToLowerInvariant()}@custodian.com"),
            new(ClaimTypes.Role, role),
            new("tenant_id", tenantId)
        };

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            claims.Add(new Claim("client_id", clientId));
            claims.Add(new Claim("clientId", clientId));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: JwtIssuer,
            audience: JwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: creds);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenantId);
        return client;
    }

    private async Task<Engagement> SeedEngagementAsync(string tenantId, string clientId, string staffId, EngagementStage stage = EngagementStage.Onboarding)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            StaffId = staffId,
            Stage = stage,
            Status = EngagementStatus.Started,
            CreatedAt = DateTime.UtcNow
        };

        db.Engagements.Add(engagement);
        await db.SaveChangesAsync();
        return engagement;
    }

    // =========================================================================
    // SCENARIO A: STAFF ATTACHES APPROVAL CONDITION VIA HTTP
    // =========================================================================

    [Fact]
    public async Task ScenarioA_StaffAttachesApprovalCondition_PersistsActivePendingAndCreatesLinkedAction()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);

        var deadline = DateTime.UtcNow.AddDays(7);
        var attachDto = new AttachConditionDto
        {
            Type = ConditionType.Approval,
            Title = "Engagement Scope Approval",
            Description = "Review and approve scope statement before execution.",
            RequiredBeforeStage = EngagementStage.Execution,
            DueDateUtc = deadline,
            InternalNote = "Confidential staff risk assessment"
        };

        // Act: POST /api/engagements/{id}/conditions
        var response = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            attachDto);

        // Assert HTTP response
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ConditionResponseDto>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.ConditionId);
        Assert.Equal(engagement.EngagementId, created.EngagementId);
        Assert.Equal("Approval", created.Type);
        Assert.True(created.IsActive);
        Assert.Equal("Pending", created.Status);
        Assert.Equal("Pending", created.ApprovalStatus);
        Assert.Equal("Engagement Scope Approval", created.Title);
        Assert.Equal(deadline.ToString("yyyy-MM-ddTHH:mm:ss"), created.DueDateUtc?.ToString("yyyy-MM-ddTHH:mm:ss"));
        Assert.Equal(clientId, created.TargetClientId); // CSTD-142: targetClient semantic verified
        Assert.Equal("Confidential staff risk assessment", created.InternalNote); // Visible to staff

        // Verify in database: Condition and Linked ClientAction
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var conditionInDb = await db.EngagementConditions.FirstOrDefaultAsync(c => c.ConditionId == created.ConditionId);
        Assert.NotNull(conditionInDb);
        Assert.True(conditionInDb!.IsActive);
        Assert.Equal(ConditionStatus.Pending, conditionInDb.Status);

        var linkedAction = await db.ClientActions.FirstOrDefaultAsync(a => a.LinkedConditionId == created.ConditionId);
        Assert.NotNull(linkedAction);
        Assert.Equal(engagement.EngagementId, linkedAction!.EngagementId);
        Assert.Equal(ClientActionType.Approval, linkedAction.Type);
        Assert.Equal(ClientActionStatus.Pending, linkedAction.Status);
        Assert.Equal(ClientActionSourceType.Condition, linkedAction.SourceType);
        Assert.Equal("Client", linkedAction.AssignedToRole);
    }

    // =========================================================================
    // SCENARIO B: CLIENT RETRIEVES APPROVAL REQUEST / STATUS VIA HTTP
    // =========================================================================

    [Fact]
    public async Task ScenarioB_IntendedClientRetrievesApprovalCondition_ReturnsClientSafeDtoWithoutInternalNotes()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Service Plan Sign-Off",
                Description = "Please sign off on the service plan",
                RequiredBeforeStage = EngagementStage.Execution,
                InternalNote = "Staff-only internal note"
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        // Act: GET /api/engagements/{id}/conditions/{conditionId} as Client
        var response = await clientHttp.GetAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}");

        // Assert: 200 OK with ClientSafeConditionDto
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Service Plan Sign-Off", root.GetProperty("title").GetString());
        Assert.Equal("Pending", root.GetProperty("status").GetString());
        Assert.Equal("Pending", root.GetProperty("approvalStatus").GetString());
        Assert.Equal("Approval", root.GetProperty("type").GetString());

        // Prove internal notes and staff metadata are strictly stripped
        Assert.False(root.TryGetProperty("internalNote", out _));
        Assert.False(root.TryGetProperty("createdBy", out _));
        Assert.False(root.TryGetProperty("targetClientId", out _));
    }

    // =========================================================================
    // SCENARIO C: INTENDED CLIENT APPROVES CONDITION VIA HTTP
    // =========================================================================

    [Fact]
    public async Task ScenarioC_IntendedClientApprovesCondition_SatisfiesConditionAndCompletesLinkedAction()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Contract Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        // Act: POST /api/engagements/{id}/conditions/{conditionId}/approve
        var approveResponse = await clientHttp.PostAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/approve",
            null);

        // Assert HTTP response
        Assert.Equal(HttpStatusCode.OK, approveResponse.StatusCode);
        var approvedDto = await approveResponse.Content.ReadFromJsonAsync<ClientSafeConditionDto>();
        Assert.NotNull(approvedDto);
        Assert.Equal("Satisfied", approvedDto!.Status);
        Assert.Equal("Approved", approvedDto.ApprovalStatus);

        // Verify DB state
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var dbCondition = await db.EngagementConditions.FirstAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal(ConditionStatus.Satisfied, dbCondition.Status);
        Assert.NotNull(dbCondition.SatisfiedAt);
        Assert.Equal(clientId, dbCondition.SatisfiedBy);

        var dbAction = await db.ClientActions.FirstAsync(a => a.LinkedConditionId == condition.ConditionId);
        Assert.Equal(ClientActionStatus.Completed, dbAction.Status);
        Assert.NotNull(dbAction.CompletedAt);

        // Repeat approve is idempotent
        var repeatResponse = await clientHttp.PostAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition.ConditionId}/approve",
            null);
        Assert.Equal(HttpStatusCode.OK, repeatResponse.StatusCode);
    }

    // =========================================================================
    // SCENARIO D: INTENDED CLIENT REJECTS CONDITION VIA HTTP
    // =========================================================================

    [Fact]
    public async Task ScenarioD_IntendedClientRejectsCondition_RetainsUnsatisfiedStateAndStoresRejectionReason()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Price Quote Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        var rejectionReason = "Budget constraints require a 15% reduction in fees.";

        // Act: POST /api/engagements/{id}/conditions/{conditionId}/reject
        var rejectResponse = await clientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/reject",
            new RejectApprovalDto { Reason = rejectionReason });

        // Assert HTTP response
        Assert.Equal(HttpStatusCode.OK, rejectResponse.StatusCode);
        var rejectedDto = await rejectResponse.Content.ReadFromJsonAsync<ClientSafeConditionDto>();
        Assert.NotNull(rejectedDto);
        Assert.Equal("Rejected", rejectedDto!.Status);
        Assert.Equal("Rejected", rejectedDto.ApprovalStatus);
        Assert.Equal(rejectionReason, rejectedDto.RejectionReason);

        // Verify DB state: condition remains unsatisfied
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();

        var dbCondition = await db.EngagementConditions.FirstAsync(c => c.ConditionId == condition.ConditionId);
        Assert.Equal(ConditionStatus.Rejected, dbCondition.Status);
        Assert.Equal(rejectionReason, dbCondition.RejectionReason);
        Assert.Null(dbCondition.SatisfiedAt); // Crucial: NOT satisfied!
        Assert.Null(dbCondition.SatisfiedBy);

        var dbAction = await db.ClientActions.FirstAsync(a => a.LinkedConditionId == condition.ConditionId);
        Assert.Equal(ClientActionStatus.Rejected, dbAction.Status);

        // Repeat reject is idempotent
        var repeatReject = await clientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition.ConditionId}/reject",
            new RejectApprovalDto { Reason = rejectionReason });
        Assert.Equal(HttpStatusCode.OK, repeatReject.StatusCode);
    }

    // =========================================================================
    // SCENARIO E: WRONG CLIENT CANNOT DECIDE
    // =========================================================================

    [Fact]
    public async Task ScenarioE_WrongClientCannotApproveOrReject_ReturnsForbidden()
    {
        var tenantId = Guid.NewGuid().ToString();
        var intendedClient = $"intended-{Guid.NewGuid():N}";
        var wrongClient = $"wrong-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, intendedClient, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var wrongClientHttp = CreateAuthenticatedClient("Client", wrongClient, tenantId, wrongClient);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Security Review Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        // Wrong client attempts to approve
        var approveResp = await wrongClientHttp.PostAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/approve",
            null);
        Assert.Equal(HttpStatusCode.Forbidden, approveResp.StatusCode);

        // Wrong client attempts to reject
        var rejectResp = await wrongClientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition.ConditionId}/reject",
            new RejectApprovalDto { Reason = "Malicious reject" });
        Assert.Equal(HttpStatusCode.Forbidden, rejectResp.StatusCode);
    }

    // =========================================================================
    // SCENARIO F: INACTIVE APPROVAL CANNOT BE DECIDED
    // =========================================================================

    [Fact]
    public async Task ScenarioF_DeactivatedApproval_CannotBeApprovedOrRejected_ReturnsConflict()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Temporary Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        // Staff deactivates the condition
        var deactResp = await staffClient.PutAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/deactivate",
            new DeactivateConditionDto { Reason = "Condition cancelled by staff" });
        Assert.Equal(HttpStatusCode.OK, deactResp.StatusCode);

        // Client attempts to approve deactivated condition -> 409 Conflict
        var approveResp = await clientHttp.PostAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition.ConditionId}/approve",
            null);
        Assert.Equal(HttpStatusCode.Conflict, approveResp.StatusCode);

        // Client attempts to reject deactivated condition -> 409 Conflict
        var rejectResp = await clientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition.ConditionId}/reject",
            new RejectApprovalDto { Reason = "Reject deactivated" });
        Assert.Equal(HttpStatusCode.Conflict, rejectResp.StatusCode);
    }

    // =========================================================================
    // SCENARIO G: CLOSED ENGAGEMENT DECISION REJECTED
    // =========================================================================

    [Fact]
    public async Task ScenarioG_ClosedEngagement_ApprovalDecisionRejected_ReturnsConflict()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Milestone Sign-Off",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        // Close the engagement in database
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkflowDbContext>();
            var eng = await db.Engagements.FirstAsync(e => e.EngagementId == engagement.EngagementId);
            eng.Status = EngagementStatus.Closed;
            await db.SaveChangesAsync();
        }

        // Client attempts to decide on closed engagement -> 409 Conflict
        var approveResp = await clientHttp.PostAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/approve",
            null);
        Assert.Equal(HttpStatusCode.Conflict, approveResp.StatusCode);

        var rejectResp = await clientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition.ConditionId}/reject",
            new RejectApprovalDto { Reason = "Reject closed" });
        Assert.Equal(HttpStatusCode.Conflict, rejectResp.StatusCode);
    }

    // =========================================================================
    // REJECTION REASON VALIDATION & ROLE ENFORCEMENT
    // =========================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectionReason_EmptyOrWhitespace_ReturnsBadRequest(string invalidReason)
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Validation Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        var response = await clientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/reject",
            new RejectApprovalDto { Reason = invalidReason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RejectionReason_Exceeding500Chars_ReturnsBadRequest()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);
        var clientHttp = CreateAuthenticatedClient("Client", clientId, tenantId, clientId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Long Reason Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        var longReason = new string('a', 501);
        var response = await clientHttp.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/reject",
            new RejectApprovalDto { Reason = longReason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NonClientRole_CallingApproveEndpoint_ReturnsForbidden()
    {
        var tenantId = Guid.NewGuid().ToString();
        var clientId = $"client-{Guid.NewGuid():N}";
        var staffId = $"staff-{Guid.NewGuid():N}";

        var engagement = await SeedEngagementAsync(tenantId, clientId, staffId);
        var staffClient = CreateAuthenticatedClient("Staff", staffId, tenantId);

        var attachResp = await staffClient.PostAsJsonAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions",
            new AttachConditionDto
            {
                Type = ConditionType.Approval,
                Title = "Role Check Approval",
                RequiredBeforeStage = EngagementStage.Execution
            });
        attachResp.EnsureSuccessStatusCode();
        var condition = await attachResp.Content.ReadFromJsonAsync<ConditionResponseDto>();

        // Staff attempts to call client-only approve endpoint
        var response = await staffClient.PostAsync(
            $"/api/engagements/{engagement.EngagementId}/conditions/{condition!.ConditionId}/approve",
            null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
