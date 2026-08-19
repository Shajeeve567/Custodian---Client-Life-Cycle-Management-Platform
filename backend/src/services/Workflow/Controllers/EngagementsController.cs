using Custodian.Workflow.Data;
using Custodian.Workflow.DTOs;
using Custodian.Workflow.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EngagementsController : ControllerBase
{
    private readonly WorkflowDbContext _dbContext;

    public EngagementsController(WorkflowDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost]
    public async Task<ActionResult<EngagementResponse>> CreateEngagement([FromBody] CreateEngagementRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var engagement = new Engagement
        {
            EngagementId = Guid.NewGuid(),
            TenantId = request.TenantId,
            ClientId = request.ClientId,
            StaffId = request.StaffId,
            Status = EngagementStatus.Draft,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Engagements.Add(engagement);
        await _dbContext.SaveChangesAsync();

        return CreatedAtAction(nameof(GetEngagementById), new { id = engagement.EngagementId, tenantId = engagement.TenantId }, MapToResponse(engagement));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EngagementResponse>> GetEngagementById(Guid id, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId parameter is required for tenant isolation.");
        }

        var engagement = await _dbContext.Engagements
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EngagementId == id && e.TenantId == tenantId);

        if (engagement == null)
        {
            return NotFound();
        }

        return Ok(MapToResponse(engagement));
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<EngagementResponse>>> GetEngagements([FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId parameter is required for tenant isolation.");
        }

        var engagements = await _dbContext.Engagements
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync();

        return Ok(engagements.Select(MapToResponse));
    }

    [HttpPut("{id}/status")]
    public async Task<ActionResult<EngagementResponse>> UpdateStatus(Guid id, [FromBody] UpdateEngagementStatusRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var engagement = await _dbContext.Engagements
            .FirstOrDefaultAsync(e => e.EngagementId == id && e.TenantId == request.TenantId);

        if (engagement == null)
        {
            return NotFound();
        }

        if (!Enum.TryParse<EngagementStatus>(request.Status, true, out var newStatus))
        {
            return BadRequest($"Invalid status: {request.Status}");
        }

        engagement.Status = newStatus;
        if (newStatus == EngagementStatus.Closed || newStatus == EngagementStatus.Cancelled)
        {
            engagement.ClosedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();
        return Ok(MapToResponse(engagement));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteEngagement(Guid id, [FromQuery] string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return BadRequest("tenantId parameter is required for tenant isolation.");
        }

        var engagement = await _dbContext.Engagements
            .FirstOrDefaultAsync(e => e.EngagementId == id && e.TenantId == tenantId);

        if (engagement == null)
        {
            return NotFound();
        }

        // Lifecycle Protection Rule: Started or Closed engagements CANNOT be physically deleted
        if (engagement.Status == EngagementStatus.Started || engagement.Status == EngagementStatus.Closed)
        {
            return Conflict(new
            {
                message = $"Engagement in state '{engagement.Status}' cannot be physically deleted per Custodian lifecycle protection rules."
            });
        }

        _dbContext.Engagements.Remove(engagement);
        await _dbContext.SaveChangesAsync();

        return NoContent();
    }

    private static EngagementResponse MapToResponse(Engagement e) => new()
    {
        EngagementId = e.EngagementId,
        TenantId = e.TenantId,
        ClientId = e.ClientId,
        StaffId = e.StaffId,
        Status = e.Status.ToString(),
        CreatedAt = e.CreatedAt,
        ClosedAt = e.ClosedAt
    };
}
