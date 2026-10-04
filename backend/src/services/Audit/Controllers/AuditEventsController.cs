using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Custodian.Shared.Messaging;
using Custodian.Audit.DTOs;
using Custodian.Audit.Services;
using Custodian.Audit.Services.EngagementAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Custodian.Audit.Controllers;

[Authorize]
[ApiController]
[Route("api/audit-events")]
[Route("api/events")]
public class AuditEventsController : ControllerBase
{
    private const string EngagementNotFound = "Engagement not found.";

    private readonly IAuditEventService _eventService;
    private readonly AuditIngestionOptions _ingestion;
    private readonly IEngagementAccessClient? _engagementAccess;

    public AuditEventsController(
        IAuditEventService eventService,
        IOptions<AuditIngestionOptions>? ingestionOptions = null,
        IEngagementAccessClient? engagementAccess = null)
    {
        _eventService = eventService;
        _ingestion = ingestionOptions?.Value ?? new AuditIngestionOptions();
        _engagementAccess = engagementAccess;
    }

    /// <summary>
    /// Staff (not Owners) only see the audit trail of engagements they are the responsible staff for;
    /// Workflow owns that assignment and is asked as the caller.
    /// </summary>
    private bool IsAssignmentRestricted => User?.IsInRole("Staff") == true && User?.IsInRole("Owner") != true;

    private static ObjectResult AccessUnavailable() => new(new
    {
        message = "Unable to confirm access to this engagement right now. Please try again shortly."
    }) { StatusCode = StatusCodes.Status503ServiceUnavailable };

    /// <summary>Null when the caller may read the engagement's trail; 404 when hidden; 503 when unconfirmed (fail closed).</summary>
    private async Task<ActionResult?> EnsureCanReadEngagementAsync(Guid engagementId, Guid tenantId)
    {
        if (!IsAssignmentRestricted) return null;
        if (_engagementAccess is null) return AccessUnavailable();

        try
        {
            var allowed = await _engagementAccess.CanAccessEngagementAsync(engagementId, tenantId, HttpContext?.RequestAborted ?? default);
            return allowed ? null : NotFound(new { message = EngagementNotFound });
        }
        catch (EngagementAccessUnavailableException)
        {
            return AccessUnavailable();
        }
    }

    /// <summary>
    /// Ingests a new append-only domain event.
    /// </summary>
    [HttpPost]
    [AllowAnonymous] // authenticated by the service ingestion key below, not by a user token
    public async Task<ActionResult<AuditEventResponse>> CreateEvent(
        [FromBody] CreateAuditEventRequest request,
        [FromQuery] string? tenantId)
    {
        // Only services may write to the tamper-evident log. A user token (any role) is not enough:
        // otherwise anyone signed in could append forged events to an engagement's chain.
        if (!HasValidIngestionKey())
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Audit events can only be written by Custodian services." });
        }

        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId, request.TenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        try
        {
            var result = await _eventService.RecordEventAsync(request, effectiveTenantId);
            return CreatedAtAction(nameof(GetEventById), new { id = result.EventId, tenantId = effectiveTenantId }, result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Custodian.Audit.Repositories.AuditChainConflictException ex)
        {
            // The event id or the engagement's chain belongs to another tenant.
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Gets all audit events for a specific engagement within the caller's tenant.
    /// </summary>
    [Authorize(Roles = "Owner,Staff")] // staff only; Staff further limited to engagements they are responsible for
    [HttpGet("engagement/{engagementId:guid}")]
    public async Task<ActionResult<IEnumerable<AuditEventResponse>>> GetEventsByEngagement(
        Guid engagementId,
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var denied = await EnsureCanReadEngagementAsync(engagementId, effectiveTenantId);
        if (denied != null)
        {
            return denied;
        }

        var results = await _eventService.GetEventsByEngagementAsync(engagementId, effectiveTenantId);
        return Ok(results);
    }

    /// <summary>
    /// Verifies the SHA-256 hash chain for a single engagement within the caller's
    /// tenant. Chains are scoped per (tenant, engagement), so each engagement
    /// starts from genesis independently. Any mismatch — payload edit, actor
    /// change, timestamp tamper, previous-hash substitution — breaks verification
    /// at the first bad event.
    /// </summary>
    [Authorize(Roles = "Owner,Staff")] // staff only; Staff further limited to engagements they are responsible for
    [HttpGet("verify")]
    public async Task<ActionResult<ChainVerificationResult>> VerifyChain(
        [FromQuery] Guid engagementId,
        [FromQuery] string? tenantId
    )
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved" });
        }

        if (engagementId == Guid.Empty)
        {
            return BadRequest(new { message = "engagementId is required" });
        }

        var denied = await EnsureCanReadEngagementAsync(engagementId, effectiveTenantId);
        if (denied != null)
        {
            return denied;
        }

        var result = await _eventService.VerifyChainAsync(effectiveTenantId, engagementId);
        return Ok(result);
    }

    /// <summary>
    /// Gets a single audit event by ID within the caller's tenant.
    /// </summary>
    [Authorize(Roles = "Owner,Staff")] // staff only; Staff further limited to engagements they are responsible for
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AuditEventResponse>> GetEventById(
        Guid id,
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var result = await _eventService.GetEventByIdAsync(id, effectiveTenantId);
        if (result == null)
        {
            return NotFound(new { message = $"Audit event with ID '{id}' was not found for tenant '{effectiveTenantId}'." });
        }

        // An event of an engagement the caller may not open is reported as not found.
        var denied = await EnsureCanReadEngagementAsync(result.EngagementId, effectiveTenantId);
        if (denied is NotFoundObjectResult)
        {
            return NotFound(new { message = $"Audit event with ID '{id}' was not found for tenant '{effectiveTenantId}'." });
        }
        if (denied != null)
        {
            return denied;
        }

        return Ok(result);
    }

    /// <summary>
    /// CSTD-42: Flags an audit event by recording non-cryptographic flag metadata and appending an immutable
    /// AuditEventFlagged reference event to the chain. Idempotent: repeated calls return the existing flagged
    /// state without appending another reference event or advancing the chain.
    /// </summary>
    [Authorize(Roles = "Owner,Staff")]
    [HttpPost("{eventId:guid}/flag")]
    public async Task<ActionResult<AuditEventResponse>> FlagEvent(
        Guid eventId,
        [FromBody] FlagAuditEventRequest request,
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { message = "Reason is required." });
        }

        var trimmedReason = request.Reason.Trim();
        if (trimmedReason.Length > 500)
        {
            return BadRequest(new { message = "Reason must not exceed 500 characters." });
        }

        var existingEvent = await _eventService.GetEventByIdAsync(eventId, effectiveTenantId);
        if (existingEvent == null)
        {
            return NotFound(new { message = $"Audit event with ID '{eventId}' was not found for tenant '{effectiveTenantId}'." });
        }

        var denied = await EnsureCanReadEngagementAsync(existingEvent.EngagementId, effectiveTenantId);
        if (denied is NotFoundObjectResult)
        {
            return NotFound(new { message = $"Audit event with ID '{eventId}' was not found for tenant '{effectiveTenantId}'." });
        }
        if (denied != null)
        {
            return denied;
        }

        var actor = ResolveActor();

        try
        {
            var result = await _eventService.FlagEventAsync(eventId, effectiveTenantId, trimmedReason, actor);
            if (result == null)
            {
                return NotFound(new { message = $"Audit event with ID '{eventId}' was not found for tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Custodian.Audit.Repositories.AuditChainConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    /// <summary>
    /// CSTD-42: Archives an audit event by updating non-cryptographic archive metadata.
    /// Idempotent: repeated calls return the existing archived state without appending an event or mutating the chain.
    /// </summary>
    [Authorize(Roles = "Owner,Staff")]
    [HttpPost("{eventId:guid}/archive")]
    public async Task<ActionResult<AuditEventResponse>> ArchiveEvent(
        Guid eventId,
        [FromBody] ArchiveAuditEventRequest? request,
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var trimmedReason = string.IsNullOrWhiteSpace(request?.Reason) ? null : request.Reason.Trim();
        if (trimmedReason != null && trimmedReason.Length > 500)
        {
            return BadRequest(new { message = "Reason must not exceed 500 characters." });
        }

        var existingEvent = await _eventService.GetEventByIdAsync(eventId, effectiveTenantId);
        if (existingEvent == null)
        {
            return NotFound(new { message = $"Audit event with ID '{eventId}' was not found for tenant '{effectiveTenantId}'." });
        }

        var denied = await EnsureCanReadEngagementAsync(existingEvent.EngagementId, effectiveTenantId);
        if (denied is NotFoundObjectResult)
        {
            return NotFound(new { message = $"Audit event with ID '{eventId}' was not found for tenant '{effectiveTenantId}'." });
        }
        if (denied != null)
        {
            return denied;
        }

        var actor = ResolveActor();

        try
        {
            var result = await _eventService.ArchiveEventAsync(eventId, effectiveTenantId, trimmedReason, actor);
            if (result == null)
            {
                return NotFound(new { message = $"Audit event with ID '{eventId}' was not found for tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Custodian.Audit.Repositories.AuditChainConflictException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    private string ResolveActor()
    {
        return User?.FindFirst(ClaimTypes.Email)?.Value
            ?? User?.FindFirst(ClaimTypes.Name)?.Value
            ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value
            ?? User?.FindFirst("email")?.Value
            ?? "system";
    }

    /// <summary>
    /// Gets all audit events for the caller's tenant.
    /// </summary>
    [Authorize(Roles = "Owner,Staff")] // staff only; Staff further limited to engagements they are responsible for
    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditEventResponse>>> GetEvents(
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (effectiveTenantId == Guid.Empty)
        {
            return BadRequest(new { message = "Tenant ID could not be resolved from JWT claim or query parameters." });
        }

        var results = await _eventService.GetEventsByTenantAsync(effectiveTenantId);
        if (!IsAssignmentRestricted)
        {
            return Ok(results);
        }

        if (_engagementAccess is null)
        {
            return AccessUnavailable();
        }

        try
        {
            var visible = await _engagementAccess.GetAccessibleEngagementIdsAsync(effectiveTenantId, HttpContext?.RequestAborted ?? default);
            return Ok(results.Where(e => visible.Contains(e.EngagementId)).ToList());
        }
        catch (EngagementAccessUnavailableException)
        {
            return AccessUnavailable();
        }
    }

    /// <summary>
    /// Resolves tenant ID server-side from HttpContext JWT claims if authenticated.
    /// Strictly rejects cross-tenant requests where a caller specifies a different tenant ID than their JWT claim.
    /// Falls back to request header/query parameter only in unauthenticated test mock contexts.
    /// </summary>
    private bool HasValidIngestionKey()
    {
        if (!AuditIngestion.IsUsableKey(_ingestion.ApiKey))
        {
            return false; // fail closed when no real key is configured (blank, short or placeholder)
        }

        var supplied = Request?.Headers[AuditIngestion.HeaderName].ToString();
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(_ingestion.ApiKey!));
    }

    private (Guid TenantId, bool IsForbidden) TryResolveTenantId(string? tenantIdQuery = null, params Guid?[] fallbackTenantIds)
    {
        var claim = User?.FindFirst("tenant_id") ?? User?.FindFirst("tenantId");
        if (claim != null && !string.IsNullOrWhiteSpace(claim.Value))
        {
            var jwtTenantGuid = StringToGuid(claim.Value.Trim());

            if (!string.IsNullOrWhiteSpace(tenantIdQuery))
            {
                var queryTenantGuid = StringToGuid(tenantIdQuery.Trim());
                if (queryTenantGuid != jwtTenantGuid)
                {
                    return (Guid.Empty, true);
                }
            }

            if (Request?.Headers.TryGetValue("X-Tenant-Id", out var tenantHeader) == true && !string.IsNullOrWhiteSpace(tenantHeader.ToString()))
            {
                var headerTenantGuid = StringToGuid(tenantHeader.ToString().Trim());
                if (headerTenantGuid != jwtTenantGuid)
                {
                    return (Guid.Empty, true);
                }
            }

            foreach (var fallback in fallbackTenantIds)
            {
                if (fallback.HasValue && fallback.Value != Guid.Empty && fallback.Value != jwtTenantGuid)
                {
                    return (Guid.Empty, true);
                }
            }

            return (jwtTenantGuid, false);
        }

        if (!string.IsNullOrWhiteSpace(tenantIdQuery))
        {
            return (StringToGuid(tenantIdQuery.Trim()), false);
        }

        if (Request?.Headers.TryGetValue("X-Tenant-Id", out var header) == true && !string.IsNullOrWhiteSpace(header.ToString()))
        {
            return (StringToGuid(header.ToString().Trim()), false);
        }

        foreach (var fallback in fallbackTenantIds)
        {
            if (fallback.HasValue && fallback.Value != Guid.Empty)
            {
                return (fallback.Value, false);
            }
        }

        return (Guid.Empty, false);
    }

    private static Guid StringToGuid(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.Empty;
        if (Guid.TryParse(value, out var parsed)) return parsed;
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        byte[] bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}