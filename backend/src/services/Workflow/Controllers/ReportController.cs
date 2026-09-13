using Custodian.Shared.Reporting.Reports;
using Custodian.Workflow.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Custodian.Workflow.Controllers;

[Authorize]
[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IEngagementRepository _repository;
    private readonly ReportGenerator _reportGenerator;

    public ReportsController(
        IEngagementRepository repository,
        ReportGenerator reportGenerator)
    {
        _repository = repository;
        _reportGenerator = reportGenerator;
    }

    [HttpGet("engagements")]
    public async Task<IActionResult> GetEngagementReport(
        [FromQuery] string tenantId,
        [FromQuery] string format = "csv")
    {
        var engagements = await _repository.GetAllByTenantAsync(tenantId);

        var definition = new ReportDefinition<Engagement>(
            "engagement-summary",
            [
                new("Engagement ID", item => item.EngagementId),
                new("Client ID", item => item.ClientId),
                new("Staff ID", item => item.StaffId),
                new("Status", item => item.Status),
                new("Stage", item => item.Stage),
                new("Created At", item => item.CreatedAt),
                new("Closed At", item => item.ClosedAt)
            ]);

        var report = format.ToLowerInvariant() switch
        {
            "csv" => _reportGenerator.GenerateCsv(definition, engagements),
            "json" => _reportGenerator.GenerateJson(definition, engagements),
            _ => throw new ArgumentException("Format must be csv or json.")
        };

        return File(
            report.Content,
            report.ContentType,
            $"{report.Name}.{format.ToLowerInvariant()}");
    }
}