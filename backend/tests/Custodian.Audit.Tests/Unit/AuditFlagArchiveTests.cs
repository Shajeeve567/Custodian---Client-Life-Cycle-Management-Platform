using System.Security.Claims;
using System.Text.Json;
using Custodian.Audit.Controllers;
using Custodian.Audit.Data;
using Custodian.Audit.DTOs;
using Custodian.Audit.Models;
using Custodian.Audit.Repositories;
using Custodian.Audit.Services;
using Custodian.Audit.Services.EngagementAccess;
using Custodian.Audit.Services.HashChain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Custodian.Audit.Tests.Unit;

/// <summary>
/// CSTD-42 (CSTD-242): Unit tests for Audit Flag and Archive commands.
/// Validates reference-event creation, SHA-256 chain integration, idempotency,
/// cryptographic immutability, role authorization, and anti-disclosure isolation.
/// </summary>
public class AuditFlagArchiveTests
{
    private readonly HashChainService _hashChainService = new();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _engagementId = Guid.NewGuid();

    private static AuditDbContext CreateDbContext(string name)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AuditDbContext(options);
    }

    private async Task<AuditEvent> CreateAndAppendEventAsync(
        IAuditEventRepository repository,
        Guid tenantId,
        Guid engagementId,
        string step = "1")
    {
        var eventId = Guid.NewGuid();
        var result = await repository.AppendToChainAsync(tenantId, engagementId, eventId, previousHash =>
        {
            var utcNow = DateTime.UtcNow;
            utcNow = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
            var payload = $"{{\"step\":\"{step}\",\"note\":\"evidence\"}}";

            var hashInput = new EventHashInput
            {
                EventId = eventId,
                EngagementId = engagementId,
                TenantId = tenantId,
                Actor = "auditor@custodian.com",
                Type = "EngagementStarted",
                Timestamp = utcNow,
                Payload = payload,
                PreviousHash = previousHash
            };

            return new AuditEvent
            {
                EventId = eventId,
                EngagementId = engagementId,
                TenantId = tenantId,
                Actor = "auditor@custodian.com",
                Type = "EngagementStarted",
                Timestamp = utcNow,
                Payload = payload,
                Hash = _hashChainService.ComputeEventHash(hashInput),
                PreviousHash = previousHash
            };
        });

        return result.Event;
    }

    // =========================================================================
    // SERVICE / REPOSITORY LEVEL TESTS (FLAG)
    // =========================================================================

    [Fact]
    public async Task Flag_CreatesReferenceEvent_EntersChain_AndPreservesOriginalEvent()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId, "42");
        var origPayload = original.Payload;
        var origHash = original.Hash;
        var origPreviousHash = original.PreviousHash;
        var origSequence = original.SequenceNumber;
        var origTimestamp = original.Timestamp;
        var origActor = original.Actor;
        var origType = original.Type;

        var reason = "Suspicious document alteration detected";
        var actor = "compliance@custodian.com";

        var response = await service.FlagEventAsync(original.EventId, _tenantId, reason, actor);

        // 1. Response validation
        Assert.NotNull(response);
        Assert.Equal(original.EventId, response!.EventId);
        Assert.True(response.IsFlagged);
        Assert.Equal(reason, response.FlagReason);
        Assert.Equal(actor, response.FlaggedBy);
        Assert.NotNull(response.FlaggedAt);
        Assert.NotNull(response.FlagReferenceEventId);

        // 2. Reference event verification in repository
        var refEvent = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == response.FlagReferenceEventId);
        Assert.NotNull(refEvent);
        Assert.Equal("AuditEventFlagged", refEvent!.Type);
        Assert.Equal(actor, refEvent.Actor);
        Assert.Equal(_tenantId, refEvent.TenantId);
        Assert.Equal(_engagementId, refEvent.EngagementId);
        Assert.Equal(origHash, refEvent.PreviousHash); // linked to previous head

        // Verify reference event payload
        using var doc = JsonDocument.Parse(refEvent.Payload);
        Assert.Equal(original.EventId.ToString(), doc.RootElement.GetProperty("referencedEventId").GetString());
        Assert.Equal(origType, doc.RootElement.GetProperty("referencedEventType").GetString());
        Assert.Equal(origSequence, doc.RootElement.GetProperty("referencedSequenceNumber").GetInt64());
        Assert.Equal(reason, doc.RootElement.GetProperty("reason").GetString());

        // 3. Cryptographic immutability of original event
        var reloadedOriginal = await db.Events.AsNoTracking().FirstAsync(e => e.EventId == original.EventId);
        Assert.Equal(origPayload, reloadedOriginal.Payload);
        Assert.Equal(origHash, reloadedOriginal.Hash);
        Assert.Equal(origPreviousHash, reloadedOriginal.PreviousHash);
        Assert.Equal(origSequence, reloadedOriginal.SequenceNumber);
        Assert.Equal(origTimestamp, reloadedOriginal.Timestamp);
        Assert.Equal(origActor, reloadedOriginal.Actor);
        Assert.Equal(origType, reloadedOriginal.Type);

        // 4. Chain verification succeeds across both events
        var chainVerification = await service.VerifyChainAsync(_tenantId, _engagementId);
        Assert.True(chainVerification.IsVerified);
        Assert.Equal(2, chainVerification.Count);
        Assert.Null(chainVerification.BrokenAtEventId);
    }

    [Fact]
    public async Task Flag_Idempotent_RepeatFlag_DoesNotAppendSecondEventOrAdvanceChain()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId, "1");

        // First flag
        var firstResponse = await service.FlagEventAsync(original.EventId, _tenantId, "Initial reason", "staff1@custodian.com");
        Assert.NotNull(firstResponse);
        var refEventId = firstResponse!.FlagReferenceEventId;
        Assert.NotNull(refEventId);

        var countAfterFirst = await db.Events.CountAsync();
        Assert.Equal(2, countAfterFirst);

        var headAfterFirst = await db.ChainHeads.AsNoTracking().FirstAsync(h => h.EngagementId == _engagementId);

        // Second flag (duplicate request / retry)
        var secondResponse = await service.FlagEventAsync(original.EventId, _tenantId, "Attempted second reason", "staff2@custodian.com");
        Assert.NotNull(secondResponse);
        Assert.Equal(refEventId, secondResponse!.FlagReferenceEventId);
        Assert.Equal("Initial reason", secondResponse.FlagReason); // preserved original reason

        var countAfterSecond = await db.Events.CountAsync();
        Assert.Equal(2, countAfterSecond); // NO second reference event created

        var headAfterSecond = await db.ChainHeads.AsNoTracking().FirstAsync(h => h.EngagementId == _engagementId);
        Assert.Equal(headAfterFirst.LastHash, headAfterSecond.LastHash); // Chain head NOT advanced
        Assert.Equal(headAfterFirst.LastEventId, headAfterSecond.LastEventId);
    }

    [Fact]
    public async Task Flag_Concurrency_MultipleParallelRequests_AppendsExactlyOneReferenceEvent()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var setupDb = CreateDbContext(dbName))
        {
            var setupRepo = new AuditEventRepository(setupDb);
            await CreateAndAppendEventAsync(setupRepo, _tenantId, _engagementId, "concurrent-test");
        }

        using var verifyDb = CreateDbContext(dbName);
        var targetEvent = await verifyDb.Events.FirstAsync();

        // Run parallel flag attempts with distinct DbContext instances on the same database
        var tasks = Enumerable.Range(1, 4).Select(async i =>
        {
            using var db = CreateDbContext(dbName);
            var repo = new AuditEventRepository(db);
            var svc = new AuditEventService(repo, _hashChainService);
            return await svc.FlagEventAsync(targetEvent.EventId, _tenantId, $"Reason {i}", $"actor{i}@custodian.com");
        });

        var results = await Task.WhenAll(tasks);

        // All parallel calls must return successful response
        Assert.All(results, r =>
        {
            Assert.NotNull(r);
            Assert.True(r!.IsFlagged);
        });

        // All must share the exact same FlagReferenceEventId
        var firstRefId = results[0]!.FlagReferenceEventId;
        Assert.NotNull(firstRefId);
        Assert.All(results, r => Assert.Equal(firstRefId, r!.FlagReferenceEventId));

        // Exactly 2 total events in DB (1 original + 1 reference event)
        using var finalDb = CreateDbContext(dbName);
        var totalEvents = await finalDb.Events.CountAsync();
        Assert.Equal(2, totalEvents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Flag_Validation_EmptyOrWhitespaceReason_ThrowsArgumentException(string? invalidReason)
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.FlagEventAsync(original.EventId, _tenantId, invalidReason!, "staff@custodian.com"));
    }

    [Fact]
    public async Task Flag_Validation_ReasonExceeding500Chars_ThrowsArgumentException()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId);
        var longReason = new string('x', 501);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.FlagEventAsync(original.EventId, _tenantId, longReason, "staff@custodian.com"));
    }

    // =========================================================================
    // SERVICE / REPOSITORY LEVEL TESTS (ARCHIVE)
    // =========================================================================

    [Fact]
    public async Task Archive_Success_UpdatesMetadataOnly_DoesNotAppendEvent_DoesNotMutateOriginal()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId, "99");
        var origPayload = original.Payload;
        var origHash = original.Hash;
        var origPreviousHash = original.PreviousHash;
        var origSequence = original.SequenceNumber;

        var headBefore = await db.ChainHeads.AsNoTracking().FirstAsync(h => h.EngagementId == _engagementId);

        var reason = "Archived per regulatory compliance retention schedule";
        var actor = "archivist@custodian.com";

        var response = await service.ArchiveEventAsync(original.EventId, _tenantId, reason, actor);

        // 1. Response validation
        Assert.NotNull(response);
        Assert.True(response!.IsArchived);
        Assert.Equal(reason, response.ArchiveReason);
        Assert.Equal(actor, response.ArchivedBy);
        Assert.NotNull(response.ArchivedAt);

        // 2. Proof NO new event was created
        var totalEvents = await db.Events.CountAsync();
        Assert.Equal(1, totalEvents);

        // 3. Proof chain head was NOT moved
        var headAfter = await db.ChainHeads.AsNoTracking().FirstAsync(h => h.EngagementId == _engagementId);
        Assert.Equal(headBefore.LastHash, headAfter.LastHash);
        Assert.Equal(headBefore.LastEventId, headAfter.LastEventId);

        // 4. Proof original event is 100% untouched
        var reloaded = await db.Events.AsNoTracking().FirstAsync(e => e.EventId == original.EventId);
        Assert.Equal(origPayload, reloaded.Payload);
        Assert.Equal(origHash, reloaded.Hash);
        Assert.Equal(origPreviousHash, reloaded.PreviousHash);
        Assert.Equal(origSequence, reloaded.SequenceNumber);

        // 5. Verification intact
        var chainResult = await service.VerifyChainAsync(_tenantId, _engagementId);
        Assert.True(chainResult.IsVerified);
        Assert.Equal(1, chainResult.Count);
    }

    [Fact]
    public async Task Archive_Idempotent_RepeatArchive_ReturnsExistingState()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId);

        var first = await service.ArchiveEventAsync(original.EventId, _tenantId, "First reason", "actor1@custodian.com");
        Assert.NotNull(first);
        Assert.True(first!.IsArchived);

        var second = await service.ArchiveEventAsync(original.EventId, _tenantId, "Second reason", "actor2@custodian.com");
        Assert.NotNull(second);
        Assert.True(second!.IsArchived);
        Assert.Equal("First reason", second.ArchiveReason); // preserved original reason

        var totalEvents = await db.Events.CountAsync();
        Assert.Equal(1, totalEvents);
    }

    [Fact]
    public async Task FlagAndArchive_CanCoexist_PreservesBothStates()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId);

        // 1. Flag event
        var flagged = await service.FlagEventAsync(original.EventId, _tenantId, "Flag reason", "flagger@custodian.com");
        Assert.True(flagged!.IsFlagged);
        Assert.False(flagged.IsArchived);
        var refId = flagged.FlagReferenceEventId;

        // 2. Archive same event
        var archived = await service.ArchiveEventAsync(original.EventId, _tenantId, "Archive reason", "archiver@custodian.com");
        Assert.True(archived!.IsFlagged); // Still flagged!
        Assert.Equal(refId, archived.FlagReferenceEventId); // Reference ID preserved!
        Assert.True(archived.IsArchived); // Now also archived!
        Assert.Equal("Archive reason", archived.ArchiveReason);
    }

    [Fact]
    public async Task CrossTenant_FlagAndArchive_ThrowsConflict()
    {
        using var db = CreateDbContext(Guid.NewGuid().ToString());
        var repository = new AuditEventRepository(db);
        var service = new AuditEventService(repository, _hashChainService);

        var original = await CreateAndAppendEventAsync(repository, _tenantId, _engagementId);

        await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            service.FlagEventAsync(original.EventId, _otherTenantId, "Cross tenant flag", "attacker@other.com"));

        await Assert.ThrowsAsync<AuditChainConflictException>(() =>
            service.ArchiveEventAsync(original.EventId, _otherTenantId, "Cross tenant archive", "attacker@other.com"));
    }

    // =========================================================================
    // CONTROLLER LEVEL TESTS (AUTHORIZATION & ANTI-DISCLOSURE)
    // =========================================================================

    private AuditEventsController CreateController(
        IAuditEventService service,
        string role,
        Guid tenantId,
        IEngagementAccessClient? accessClient = null)
    {
        var controller = new AuditEventsController(service, null, accessClient);
        var claims = new List<Claim>
        {
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.Role, role),
            new(ClaimTypes.NameIdentifier, $"{role.ToLowerInvariant()}-user-1"),
            new(ClaimTypes.Email, $"{role.ToLowerInvariant()}@custodian.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task Controller_Staff_AssignedEngagement_CanFlagEvent()
    {
        var mockService = new Mock<IAuditEventService>();
        var mockAccess = new Mock<IEngagementAccessClient>();
        var eventId = Guid.NewGuid();

        var existingEvent = new AuditEventResponse
        {
            EventId = eventId,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "user",
            Type = "Type",
            Payload = "{}"
        };

        mockService.Setup(s => s.GetEventByIdAsync(eventId, _tenantId)).ReturnsAsync(existingEvent);
        mockAccess.Setup(a => a.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        mockService.Setup(s => s.FlagEventAsync(eventId, _tenantId, "Valid reason", It.IsAny<string>()))
            .ReturnsAsync(new AuditEventResponse
            {
                EventId = eventId,
                EngagementId = _engagementId,
                TenantId = _tenantId,
                IsFlagged = true,
                FlagReason = "Valid reason"
            });

        var controller = CreateController(mockService.Object, "Staff", _tenantId, mockAccess.Object);
        var result = await controller.FlagEvent(eventId, new FlagAuditEventRequest { Reason = "Valid reason" }, null);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var res = Assert.IsType<AuditEventResponse>(okResult.Value);
        Assert.True(res.IsFlagged);
    }

    [Fact]
    public async Task Controller_Staff_UnassignedEngagement_ReturnsNotFound_AntiDisclosure()
    {
        var mockService = new Mock<IAuditEventService>();
        var mockAccess = new Mock<IEngagementAccessClient>();
        var eventId = Guid.NewGuid();

        var existingEvent = new AuditEventResponse
        {
            EventId = eventId,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "user",
            Type = "Type",
            Payload = "{}"
        };

        mockService.Setup(s => s.GetEventByIdAsync(eventId, _tenantId)).ReturnsAsync(existingEvent);
        // Workflow reports staff is NOT assigned to this engagement:
        mockAccess.Setup(a => a.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var controller = CreateController(mockService.Object, "Staff", _tenantId, mockAccess.Object);
        var result = await controller.FlagEvent(eventId, new FlagAuditEventRequest { Reason = "Valid reason" }, null);

        // Anti-disclosure: returns 404
        Assert.IsType<NotFoundObjectResult>(result.Result);
        mockService.Verify(s => s.FlagEventAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Controller_Owner_CanFlagWithoutEngagementAccessCheck()
    {
        var mockService = new Mock<IAuditEventService>();
        var mockAccess = new Mock<IEngagementAccessClient>();
        var eventId = Guid.NewGuid();

        var existingEvent = new AuditEventResponse
        {
            EventId = eventId,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "user",
            Type = "Type",
            Payload = "{}"
        };

        mockService.Setup(s => s.GetEventByIdAsync(eventId, _tenantId)).ReturnsAsync(existingEvent);
        mockService.Setup(s => s.FlagEventAsync(eventId, _tenantId, "Owner reason", It.IsAny<string>()))
            .ReturnsAsync(new AuditEventResponse
            {
                EventId = eventId,
                EngagementId = _engagementId,
                TenantId = _tenantId,
                IsFlagged = true,
                FlagReason = "Owner reason"
            });

        var controller = CreateController(mockService.Object, "Owner", _tenantId, mockAccess.Object);
        var result = await controller.FlagEvent(eventId, new FlagAuditEventRequest { Reason = "Owner reason" }, null);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(okResult.Value);
        mockAccess.Verify(a => a.CanAccessEngagementAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Controller_Flag_EmptyReason_ReturnsBadRequest()
    {
        var mockService = new Mock<IAuditEventService>();
        var controller = CreateController(mockService.Object, "Owner", _tenantId);

        var result = await controller.FlagEvent(Guid.NewGuid(), new FlagAuditEventRequest { Reason = "   " }, null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task Controller_Flag_ReasonTooLong_ReturnsBadRequest()
    {
        var mockService = new Mock<IAuditEventService>();
        var controller = CreateController(mockService.Object, "Owner", _tenantId);

        var result = await controller.FlagEvent(Guid.NewGuid(), new FlagAuditEventRequest { Reason = new string('x', 501) }, null);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequest.Value);
    }

    [Fact]
    public async Task Controller_Archive_Staff_AssignedEngagement_CanArchiveEvent()
    {
        var mockService = new Mock<IAuditEventService>();
        var mockAccess = new Mock<IEngagementAccessClient>();
        var eventId = Guid.NewGuid();

        var existingEvent = new AuditEventResponse
        {
            EventId = eventId,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "user",
            Type = "Type",
            Payload = "{}"
        };

        mockService.Setup(s => s.GetEventByIdAsync(eventId, _tenantId)).ReturnsAsync(existingEvent);
        mockAccess.Setup(a => a.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        mockService.Setup(s => s.ArchiveEventAsync(eventId, _tenantId, "Retention reason", It.IsAny<string>()))
            .ReturnsAsync(new AuditEventResponse
            {
                EventId = eventId,
                EngagementId = _engagementId,
                TenantId = _tenantId,
                IsArchived = true,
                ArchiveReason = "Retention reason"
            });

        var controller = CreateController(mockService.Object, "Staff", _tenantId, mockAccess.Object);
        var result = await controller.ArchiveEvent(eventId, new ArchiveAuditEventRequest { Reason = "Retention reason" }, null);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var res = Assert.IsType<AuditEventResponse>(okResult.Value);
        Assert.True(res.IsArchived);
    }

    [Fact]
    public async Task Controller_Archive_Staff_UnassignedEngagement_ReturnsNotFound_AntiDisclosure()
    {
        var mockService = new Mock<IAuditEventService>();
        var mockAccess = new Mock<IEngagementAccessClient>();
        var eventId = Guid.NewGuid();

        var existingEvent = new AuditEventResponse
        {
            EventId = eventId,
            EngagementId = _engagementId,
            TenantId = _tenantId,
            Actor = "user",
            Type = "Type",
            Payload = "{}"
        };

        mockService.Setup(s => s.GetEventByIdAsync(eventId, _tenantId)).ReturnsAsync(existingEvent);
        mockAccess.Setup(a => a.CanAccessEngagementAsync(_engagementId, _tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var controller = CreateController(mockService.Object, "Staff", _tenantId, mockAccess.Object);
        var result = await controller.ArchiveEvent(eventId, new ArchiveAuditEventRequest { Reason = "Reason" }, null);

        Assert.IsType<NotFoundObjectResult>(result.Result);
        mockService.Verify(s => s.ArchiveEventAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Controller_CrossTenantCaller_ReturnsForbidden()
    {
        var mockService = new Mock<IAuditEventService>();
        var controller = CreateController(mockService.Object, "Owner", _tenantId);

        // Caller's JWT has _tenantId, but query tries to specify _otherTenantId
        var result = await controller.FlagEvent(Guid.NewGuid(), new FlagAuditEventRequest { Reason = "Reason" }, _otherTenantId.ToString());

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Theory]
    [InlineData(nameof(AuditEventsController.FlagEvent))]
    [InlineData(nameof(AuditEventsController.ArchiveEvent))]
    public void CommandEndpoints_AreRestrictedToOwnerAndStaff(string methodName)
    {
        var method = typeof(AuditEventsController).GetMethod(methodName)!;
        var authAttr = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .FirstOrDefault();

        Assert.NotNull(authAttr);
        Assert.Equal("Owner,Staff", authAttr.Roles);
    }
}
