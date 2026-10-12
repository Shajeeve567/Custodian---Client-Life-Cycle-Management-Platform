using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Custodian.Audit.Data;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.HashChain;
using Custodian.Shared.Messaging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Integration;

/// <summary>
/// CSTD-42 / CSTD-244: Real ASP.NET Core HTTP integration tests for Flag and Archive endpoints.
/// Exercises real routing, JWT authentication, tenant claims, role authorization,
/// Staff engagement anti-disclosure, repository, EF DbContext, idempotency,
/// original event cryptographic immutability, reference event creation,
/// and SHA-256 chain verification integrity.
/// Runs deterministically locally and in CI.
/// </summary>
public class AuditFlagArchiveEndpointTests : IDisposable
{
    private readonly AuditTestFixture _fixture;
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _engagementId = Guid.NewGuid();
    private readonly HashChainService _hashChainService = new();

    public AuditFlagArchiveEndpointTests()
    {
        _fixture = new AuditTestFixture();
        _client = _fixture.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _fixture.Dispose();
    }

    private void AuthenticateClient(HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<AuditEvent> SeedEventAsync(Guid tenantId, Guid engagementId, string step = "1", string type = "EngagementStarted")
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var eventId = Guid.NewGuid();
        var utcNow = DateTime.UtcNow;
        utcNow = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));

        var existingHead = await db.ChainHeads.FirstOrDefaultAsync(h => h.EngagementId == engagementId);
        var previousHash = existingHead?.LastHash ?? _hashChainService.GenesisHash;

        var payload = $"{{\"step\":\"{step}\",\"timestamp\":\"{utcNow:O}\"}}";
        var hashInput = new EventHashInput
        {
            EventId = eventId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "seeder@custodian.com",
            Type = type,
            Timestamp = utcNow,
            Payload = payload,
            PreviousHash = previousHash
        };

        var auditEvent = new AuditEvent
        {
            EventId = eventId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Actor = "seeder@custodian.com",
            Type = type,
            Timestamp = utcNow,
            Payload = payload,
            Hash = _hashChainService.ComputeEventHash(hashInput),
            PreviousHash = previousHash,
            SequenceNumber = (existingHead != null ? await db.Events.CountAsync(e => e.EngagementId == engagementId) : 0) + 1
        };

        db.Events.Add(auditEvent);
        if (existingHead == null)
        {
            db.ChainHeads.Add(new EngagementChainHead
            {
                EngagementId = engagementId,
                TenantId = tenantId,
                LastEventId = eventId,
                LastHash = auditEvent.Hash,
                UpdatedAt = utcNow
            });
        }
        else
        {
            existingHead.LastEventId = eventId;
            existingHead.LastHash = auditEvent.Hash;
            existingHead.UpdatedAt = utcNow;
        }

        await db.SaveChangesAsync();
        return auditEvent;
    }

    // =========================================================================
    // 1. OWNER AUTHORIZATION & FLAG FUNCTIONALITY
    // =========================================================================

    [Fact]
    public async Task Owner_FlagEvent_Success_CreatesReferenceEvent_PreservesOriginal_AndChainVerifies()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId, "step-1");
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString(), "owner@custodian.com");
        AuthenticateClient(_client, token);

        var requestBody = new FlagAuditEventRequest { Reason = "Document tamper suspected" };
        var response = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag", requestBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.NotNull(result);
        Assert.Equal(seeded.EventId, result!.EventId);
        Assert.True(result.IsFlagged);
        Assert.Equal("Document tamper suspected", result.FlagReason);
        Assert.Equal("owner@custodian.com", result.FlaggedBy);
        Assert.NotNull(result.FlaggedAt);
        Assert.NotNull(result.FlagReferenceEventId);

        // Verify Database state
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Original event remains 100% byte-for-byte cryptographically unchanged
        var originalInDb = await db.Events.AsNoTracking().FirstAsync(e => e.EventId == seeded.EventId);
        Assert.Equal(seeded.Payload, originalInDb.Payload);
        Assert.Equal(seeded.Hash, originalInDb.Hash);
        Assert.Equal(seeded.PreviousHash, originalInDb.PreviousHash);
        Assert.Equal(seeded.SequenceNumber, originalInDb.SequenceNumber);
        Assert.Equal(seeded.Timestamp, originalInDb.Timestamp);

        // Reference event exists in DB
        var refEvent = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == result.FlagReferenceEventId);
        Assert.NotNull(refEvent);
        Assert.Equal("AuditEventFlagged", refEvent!.Type);
        Assert.Equal(_tenantId, refEvent.TenantId);
        Assert.Equal(_engagementId, refEvent.EngagementId);
        Assert.Equal(seeded.Hash, refEvent.PreviousHash); // Appended immediately after original

        using var doc = JsonDocument.Parse(refEvent.Payload);
        Assert.Equal(seeded.EventId.ToString(), doc.RootElement.GetProperty("referencedEventId").GetString());
        Assert.Equal("Document tamper suspected", doc.RootElement.GetProperty("reason").GetString());

        // Verification endpoint confirms chain is valid with both events
        var verifyResp = await _client.GetAsync($"/api/audit-events/verify?engagementId={_engagementId}");
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        var verifyResult = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationResult>();
        Assert.NotNull(verifyResult);
        Assert.True(verifyResult!.IsVerified);
        Assert.Equal(2, verifyResult.Count);
        Assert.Null(verifyResult.BrokenAtEventId);
    }

    // =========================================================================
    // 2. STAFF AUTHORIZATION & ANTI-DISCLOSURE (403 vs 404)
    // =========================================================================

    [Fact]
    public async Task Staff_AssignedEngagement_CanFlagEvent()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var staffEmail = "assigned-staff@custodian.com";
        var token = TestTokenFactory.CreateStaffToken(_tenantId.ToString(), staffEmail);
        AuthenticateClient(_client, token);

        // Workflow permits access for this staff
        _fixture.MockEngagementAccess
            .Setup(x => x.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var response = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Staff flagged issue" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.NotNull(result);
        Assert.True(result!.IsFlagged);
        Assert.Equal("Staff flagged issue", result.FlagReason);
        Assert.Equal(staffEmail, result.FlaggedBy);
    }

    [Fact]
    public async Task Staff_UnassignedEngagement_ReturnsNotFound_AntiDisclosure()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateStaffToken(_tenantId.ToString(), "unassigned@custodian.com");
        AuthenticateClient(_client, token);

        // Workflow denies access -> must return 404 to avoid disclosure
        _fixture.MockEngagementAccess
            .Setup(x => x.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var response = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Attempted flag" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Proof no metadata or reference event was added
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var meta = await db.EventMetadata.FirstOrDefaultAsync(m => m.EventId == seeded.EventId);
        Assert.Null(meta);
        var count = await db.Events.CountAsync();
        Assert.Equal(1, count);
    }

    // =========================================================================
    // 3. OWNER ARCHIVE FUNCTIONALITY & VERIFICATION INVARIANCE (CSTD-243)
    // =========================================================================

    [Fact]
    public async Task Owner_ArchiveEvent_Success_ModifiesMetadataOnly_PreservesVerification()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId, "step-archive");
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString(), "owner@custodian.com");
        AuthenticateClient(_client, token);

        var archiveReason = "Record retention period completed";
        var response = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = archiveReason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.NotNull(result);
        Assert.True(result!.IsArchived);
        Assert.Equal(archiveReason, result.ArchiveReason);
        Assert.Equal("owner@custodian.com", result.ArchivedBy);
        Assert.NotNull(result.ArchivedAt);

        // Cryptographic immutability and zero-chain-mutation checks
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Proof 1: No new event was appended to the chain
        var totalEvents = await db.Events.CountAsync();
        Assert.Equal(1, totalEvents);

        // Proof 2: Chain head was NOT moved
        var head = await db.ChainHeads.FirstAsync(h => h.EngagementId == _engagementId);
        Assert.Equal(seeded.Hash, head.LastHash);
        Assert.Equal(seeded.EventId, head.LastEventId);

        // Proof 3: Original event is 100% untouched
        var reloaded = await db.Events.FirstAsync(e => e.EventId == seeded.EventId);
        Assert.Equal(seeded.Payload, reloaded.Payload);
        Assert.Equal(seeded.Hash, reloaded.Hash);
        Assert.Equal(seeded.PreviousHash, reloaded.PreviousHash);
        Assert.Equal(seeded.SequenceNumber, reloaded.SequenceNumber);

        // Proof 4 (CSTD-243): Archived event remains fully part of chain verification
        var verifyResp = await _client.GetAsync($"/api/audit-events/verify?engagementId={_engagementId}");
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        var verifyResult = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationResult>();
        Assert.NotNull(verifyResult);
        Assert.True(verifyResult!.IsVerified);
        Assert.Equal(1, verifyResult.Count);
        Assert.Null(verifyResult.BrokenAtEventId);
    }

    [Fact]
    public async Task Staff_AssignedEngagement_CanArchiveEvent()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var staffEmail = "staff-archivist@custodian.com";
        var token = TestTokenFactory.CreateStaffToken(_tenantId.ToString(), staffEmail);
        AuthenticateClient(_client, token);

        _fixture.MockEngagementAccess
            .Setup(x => x.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var response = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "Staff archived" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.NotNull(result);
        Assert.True(result!.IsArchived);
        Assert.Equal("Staff archived", result.ArchiveReason);
        Assert.Equal(staffEmail, result.ArchivedBy);
    }

    // =========================================================================
    // 4. CLIENT ROLE FORBIDDEN (SECURITY CONTRACT)
    // =========================================================================

    [Fact]
    public async Task ClientRole_CannotFlagOrArchive_ReturnsForbidden()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateClientToken(_tenantId.ToString(), "client@custodian.com");
        AuthenticateClient(_client, token);

        var flagResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Unauthorized attempt" });
        Assert.Equal(HttpStatusCode.Forbidden, flagResp.StatusCode);

        var archiveResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "Unauthorized attempt" });
        Assert.Equal(HttpStatusCode.Forbidden, archiveResp.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_CannotFlagOrArchive_ReturnsUnauthorized()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        _client.DefaultRequestHeaders.Authorization = null;

        var flagResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "No auth" });
        Assert.Equal(HttpStatusCode.Unauthorized, flagResp.StatusCode);

        var archiveResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "No auth" });
        Assert.Equal(HttpStatusCode.Unauthorized, archiveResp.StatusCode);
    }

    // =========================================================================
    // 5. TENANT ISOLATION & MISMATCH HANDLING
    // =========================================================================

    [Fact]
    public async Task CrossTenant_TokenQueryMismatch_ReturnsForbidden()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        // Token belongs to _tenantId, query specifies _otherTenantId
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        var response = await _client.PostAsJsonAsync(
            $"/api/audit-events/{seeded.EventId}/flag?tenantId={_otherTenantId}",
            new FlagAuditEventRequest { Reason = "Mismatch" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task CrossTenant_OtherTenantCannotFlagOrArchiveEvent_ReturnsNotFound()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        // Token belongs to _otherTenantId
        var token = TestTokenFactory.CreateOwnerToken(_otherTenantId.ToString());
        AuthenticateClient(_client, token);

        var flagResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Cross tenant attack" });
        Assert.Equal(HttpStatusCode.NotFound, flagResp.StatusCode);

        var archiveResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "Cross tenant attack" });
        Assert.Equal(HttpStatusCode.NotFound, archiveResp.StatusCode);
    }

    // =========================================================================
    // 6. IDEMPOTENCY & DUPLICATE CALL SAFETY
    // =========================================================================

    [Fact]
    public async Task Flag_RepeatedCall_IsIdempotent_DoesNotDuplicateReferenceEvent()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        // First flag call
        var resp1 = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Original flag reason" });
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var dto1 = await resp1.Content.ReadFromJsonAsync<AuditEventResponse>();
        var refId = dto1!.FlagReferenceEventId;
        Assert.NotNull(refId);

        // Repeated flag call (retry / duplicate)
        var resp2 = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Second attempt reason" });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var dto2 = await resp2.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.Equal(refId, dto2!.FlagReferenceEventId);
        Assert.Equal("Original flag reason", dto2.FlagReason);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        // Total events in database MUST strictly be 2 (1 original + 1 reference event)
        var count = await db.Events.CountAsync();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Archive_RepeatedCall_IsIdempotent_PreservesState()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        var resp1 = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "First archive" });
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var resp2 = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "Second archive" });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var dto2 = await resp2.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.True(dto2!.IsArchived);
        Assert.Equal("First archive", dto2.ArchiveReason);

        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.Equal(1, await db.Events.CountAsync());
    }

    // =========================================================================
    // 7. FLAG AND ARCHIVE COEXISTENCE
    // =========================================================================

    [Fact]
    public async Task Event_CanBeBothFlaggedAndArchived_MaintainsBothStates()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        // Flag event
        var flagResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Flagged for investigation" });
        Assert.Equal(HttpStatusCode.OK, flagResp.StatusCode);

        // Archive same event
        var archResp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/archive",
            new ArchiveAuditEventRequest { Reason = "Archived case" });
        Assert.Equal(HttpStatusCode.OK, archResp.StatusCode);

        var dto = await archResp.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.NotNull(dto);
        Assert.True(dto!.IsFlagged);
        Assert.Equal("Flagged for investigation", dto.FlagReason);
        Assert.NotNull(dto.FlagReferenceEventId);
        Assert.True(dto.IsArchived);
        Assert.Equal("Archived case", dto.ArchiveReason);

        // Verify chain verification stays valid across both
        var verifyResp = await _client.GetAsync($"/api/audit-events/verify?engagementId={_engagementId}");
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        var verify = await verifyResp.Content.ReadFromJsonAsync<ChainVerificationResult>();
        Assert.True(verify!.IsVerified);
        Assert.Equal(2, verify.Count);
    }

    // =========================================================================
    // 8. INPUT VALIDATION (BAD REQUEST)
    // =========================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Flag_EmptyOrWhitespaceReason_ReturnsBadRequest(string invalidReason)
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        var resp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = invalidReason });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Flag_ReasonTooLong_ReturnsBadRequest()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        var resp = await _client.PostAsJsonAsync($"/api/audit-events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = new string('a', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // =========================================================================
    // 9. ROUTE ALIAS: /api/events/{id}/flag
    // =========================================================================

    [Fact]
    public async Task AlternateRoute_ApiEvents_WorksIdentically()
    {
        var seeded = await SeedEventAsync(_tenantId, _engagementId);
        var token = TestTokenFactory.CreateOwnerToken(_tenantId.ToString());
        AuthenticateClient(_client, token);

        var resp = await _client.PostAsJsonAsync($"/api/events/{seeded.EventId}/flag",
            new FlagAuditEventRequest { Reason = "Via alternate route" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<AuditEventResponse>();
        Assert.True(dto!.IsFlagged);
    }
}

/// <summary>
/// WebApplicationFactory fixture configuring in-memory EF DbContext and mock Workflow access client.
/// </summary>
public class AuditTestFixture : WebApplicationFactory<Program>
{
    public Mock<IEngagementAccessClient> MockEngagementAccess { get; } = new();
    public string DatabaseName { get; } = "IntegAuditDb-" + Guid.NewGuid();
    private readonly ServiceProvider _inMemoryProvider = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(AuditIngestion.ConfigKey, "integration-test-ingestion-key-0123456789");
        builder.ConfigureServices(services =>
        {
            var descriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<AuditDbContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(AuditDbContext)).ToList();

            foreach (var d in descriptors)
            {
                services.Remove(d);
            }

            services.AddDbContext<AuditDbContext>(options =>
            {
                options.UseInMemoryDatabase(DatabaseName);
                options.UseInternalServiceProvider(_inMemoryProvider);
            });

            var accessDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEngagementAccessClient));
            if (accessDescriptor != null)
            {
                services.Remove(accessDescriptor);
            }
            services.AddScoped<IEngagementAccessClient>(_ => MockEngagementAccess.Object);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _inMemoryProvider.Dispose();
        }
    }
}
