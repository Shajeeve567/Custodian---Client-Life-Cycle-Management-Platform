using Custodian.Workflow.DTOs;
using Custodian.Workflow.Services.Meetings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize(Roles = "Owner,Staff")]
[ApiController]
[Route("api/engagements/{engagementId:guid}/meetings")]
public class MeetingsController : ControllerBase
{
    private readonly IMeetingService _meetings;

    public MeetingsController(IMeetingService meetings)
    {
        _meetings = meetings;
    }

    [HttpPost]
    public async Task<ActionResult<MeetingResponse>> Create(
        [FromRoute] Guid engagementId,
        [FromBody] CreateMeetingRequest request,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId)) return BadRequest(new { message = "Tenant identification is required." });
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            var result = await _meetings.CreateAsync(engagementId, effectiveTenantId, ResolveActor(), request, ct);
            return CreatedAtAction(nameof(Get), new { engagementId, meetingId = result.MeetingId, tenantId = effectiveTenantId }, result);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MeetingResponse>>> List(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId)) return BadRequest(new { message = "Tenant identification is required." });

        return Ok(await _meetings.ListForEngagementAsync(engagementId, effectiveTenantId, ct));
    }

    [HttpGet("{meetingId:guid}")]
    public async Task<ActionResult<MeetingResponse>> Get(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid meetingId,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId)) return BadRequest(new { message = "Tenant identification is required." });

        var result = await _meetings.GetAsync(engagementId, meetingId, effectiveTenantId, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpPut("{meetingId:guid}")]
    public async Task<ActionResult<MeetingResponse>> Update(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid meetingId,
        [FromBody] UpdateMeetingRequest request,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId)) return BadRequest(new { message = "Tenant identification is required." });
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            var result = await _meetings.UpdateAsync(engagementId, meetingId, effectiveTenantId, ResolveActor(), request, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{meetingId:guid}/status")]
    public async Task<ActionResult<MeetingResponse>> UpdateStatus(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid meetingId,
        [FromBody] UpdateMeetingStatusRequest request,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId)) return BadRequest(new { message = "Tenant identification is required." });
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            var result = await _meetings.UpdateStatusAsync(engagementId, meetingId, effectiveTenantId, ResolveActor(), request.Status, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("{meetingId:guid}/reschedule")]
    public async Task<ActionResult<MeetingResponse>> Reschedule(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid meetingId,
        [FromBody] RescheduleMeetingRequest request,
        [FromQuery] string? tenantId,
        CancellationToken ct = default)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden) return Forbid();
        if (string.IsNullOrWhiteSpace(effectiveTenantId)) return BadRequest(new { message = "Tenant identification is required." });
        if (!ModelState.IsValid) return BadRequest(ModelState);

        try
        {
            var result = await _meetings.RescheduleAsync(engagementId, meetingId, effectiveTenantId, ResolveActor(), request, ct);
            return result is null ? NotFound() : Ok(result);
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }

    private string ResolveActor() =>
        User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User?.FindFirst("sub")?.Value
        ?? "System";

    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? queryTenantId)
    {
        var jwtTenantId = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;

        if (!string.IsNullOrWhiteSpace(jwtTenantId))
        {
            var cleanJwtTenant = jwtTenantId.Trim();
            if (!string.IsNullOrWhiteSpace(queryTenantId)
                && !string.Equals(queryTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
                return (null, true);
            return (cleanJwtTenant, false);
        }

        return (!string.IsNullOrWhiteSpace(queryTenantId) ? queryTenantId.Trim() : null, false);
    }
}