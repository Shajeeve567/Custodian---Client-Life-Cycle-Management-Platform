using Custodian.Documents.DTOs;
using Custodian.Documents.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Custodian.Documents.Controllers;

[ApiController]
[Route("api/engagements/{engagementId:guid}/documents")]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IStorageService _storageService;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        IDocumentService documentService,
        IStorageService storageService,
        ILogger<DocumentsController> logger)
    {
        _documentService = documentService;
        _storageService = storageService;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a PDF document and creates metadata record for the specified engagement.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<DocumentResponseDto>> UploadDocument(
        [FromRoute] Guid engagementId,
        [FromForm] DocumentUploadDto uploadDto,
        [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var result = await _documentService.UploadDocumentAsync(engagementId, effectiveTenantId, uploadDto);
            return CreatedAtAction(
                nameof(GetDocumentById),
                new { engagementId, documentId = result.DocumentId, tenantId = effectiveTenantId },
                result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Document upload validation failed for engagement {EngagementId}", engagementId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Lists all document metadata records associated with the specified engagement.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DocumentResponseDto>>> GetDocumentsByEngagement(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
        }

        var results = await _documentService.GetDocumentsByEngagementAsync(engagementId, effectiveTenantId);
        return Ok(results);
    }

    /// <summary>
    /// Retrieves metadata for a specific document.
    /// </summary>
    [HttpGet("{documentId:guid}")]
    public async Task<ActionResult<DocumentResponseDto>> GetDocumentById(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
        }

        var result = await _documentService.GetDocumentByIdAsync(engagementId, documentId, effectiveTenantId);
        if (result == null)
        {
            return NotFound(new { message = $"Document '{documentId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
        }

        return Ok(result);
    }

    /// <summary>
    /// Downloads the binary PDF content for a specific document.
    /// </summary>
    [HttpGet("{documentId:guid}/download")]
    public async Task<IActionResult> DownloadDocument(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromQuery] string? tenantId)
    {
        var effectiveTenantId = ResolveTenantId(tenantId);
        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via X-Tenant-ID header, JWT claim, or tenantId parameter." });
        }

        var metadata = await _documentService.GetDocumentByIdAsync(engagementId, documentId, effectiveTenantId);
        if (metadata == null)
        {
            return NotFound(new { message = $"Document metadata for '{documentId}' was not found." });
        }

        var fileStream = await _storageService.GetFileAsync(metadata.StoragePath);
        if (fileStream == null)
        {
            return NotFound(new { message = $"Binary file content for document '{documentId}' was not found in storage." });
        }

        return File(fileStream, metadata.ContentType ?? "application/pdf", metadata.FileName);
    }

    /// <summary>
    /// Resolves tenant ID from HTTP X-Tenant-ID header, JWT claims, or query parameter fallback.
    /// </summary>
    private string? ResolveTenantId(string? queryTenantId)
    {
        // 1. Check HTTP header X-Tenant-ID
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            var headerTenant = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerTenant))
            {
                return headerTenant.Trim();
            }
        }

        // 2. Check JWT Claims
        var jwtClaimTenant = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaimTenant))
        {
            return jwtClaimTenant.Trim();
        }

        // 3. Fallback to Query String Parameter
        if (!string.IsNullOrWhiteSpace(queryTenantId))
        {
            return queryTenantId.Trim();
        }

        return null;
    }
}
