using Custodian.Documents.DTOs;
using Custodian.Documents.Services;
using Custodian.Documents.Services.EngagementAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;


namespace Custodian.Documents.Controllers;

[Authorize]
[ApiController]
[Route("api/engagements/{engagementId:guid}/documents")]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentService _documentService;
    private readonly IStorageService _storageService;
    private readonly ILogger<DocumentsController> _logger;
    private readonly IEngagementAccessClient _engagementAccess;

    public DocumentsController(
        IDocumentService documentService,
        IStorageService storageService,
        ILogger<DocumentsController> logger,
        IEngagementAccessClient engagementAccess)
    {
        _documentService = documentService;
        _storageService = storageService;
        _logger = logger;
        _engagementAccess = engagementAccess;
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
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var accessDenied = await EnsureClientCanAccessEngagementAsync(engagementId, effectiveTenantId);
        if (accessDenied != null)
        {
            return accessDenied;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            // Uploader identity comes from the JWT, not the form.
            uploadDto.UploaderId = ResolveStaffActor() ?? uploadDto.UploaderId;
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
    /// Lists document metadata records associated with the specified engagement, supporting query filtering and soft-delete visibility.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<DocumentResponseDto>>> GetDocumentsByEngagement(
        [FromRoute] Guid engagementId,
        [FromQuery] string? tenantId = null,
        [FromQuery] DocumentFilterDto? filter = null)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var accessDenied = await EnsureClientCanAccessEngagementAsync(engagementId, effectiveTenantId);
        if (accessDenied != null)
        {
            return accessDenied;
        }

        var results = filter != null
            ? await _documentService.GetDocumentsByEngagementAsync(engagementId, effectiveTenantId, filter)
            : await _documentService.GetDocumentsByEngagementAsync(engagementId, effectiveTenantId);
        return Ok(results);
    }

    /// <summary>
    /// Retrieves metadata for a specific document. Soft-deleted documents return 404 unless includeDeleted is true.
    /// </summary>
    [HttpGet("{documentId:guid}")]
    public async Task<ActionResult<DocumentResponseDto>> GetDocumentById(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromQuery] string? tenantId = null,
        [FromQuery] bool includeDeleted = false)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var accessDenied = await EnsureClientCanAccessEngagementAsync(engagementId, effectiveTenantId);
        if (accessDenied != null)
        {
            return accessDenied;
        }

        var result = includeDeleted
            ? await _documentService.GetDocumentByIdAsync(engagementId, documentId, effectiveTenantId, includeDeleted: true)
            : await _documentService.GetDocumentByIdAsync(engagementId, documentId, effectiveTenantId);
        if (result == null)
        {
            return NotFound(new { message = $"Document '{documentId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
        }

        return Ok(result);
    }

    /// <summary>
    /// Downloads the binary content for a specific document returning original bytes and content type.
    /// Soft-deleted documents return 404 unless includeDeleted is true.
    /// </summary>
    [HttpGet("{documentId:guid}/download")]
    public async Task<IActionResult> DownloadDocument(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromQuery] string? tenantId = null,
        [FromQuery] bool includeDeleted = false)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var accessDenied = await EnsureClientCanAccessEngagementAsync(engagementId, effectiveTenantId);
        if (accessDenied != null)
        {
            return accessDenied;
        }

        var metadata = includeDeleted
            ? await _documentService.GetDocumentByIdAsync(engagementId, documentId, effectiveTenantId, includeDeleted: true)
            : await _documentService.GetDocumentByIdAsync(engagementId, documentId, effectiveTenantId);
        if (metadata == null)
        {
            return NotFound(new { message = $"Document metadata for '{documentId}' was not found." });
        }

        var fileStream = await _storageService.GetFileAsync(metadata.StoragePath);
        if (fileStream == null)
        {
            return NotFound(new { message = $"Binary file content for document '{documentId}' was not found in storage." });
        }

        var contentType = !string.IsNullOrWhiteSpace(metadata.ContentType) ? metadata.ContentType : "application/octet-stream";
        return File(fileStream, contentType, metadata.FileName);
    }

    /// <summary>
    /// Staff verification endpoint: Manually marks an automatically compliant document as Verified.
    /// Restricted to authorized staff (Owner, Staff).
    /// Precondition: Document must already be automatically compliant.
    /// </summary>
    [HttpPost("{documentId:guid}/verify")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<DocumentResponseDto>> VerifyDocument(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromBody] VerifyDocumentRequestDto dto,
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var authResult = CheckStaffAuthorization();
        if (authResult != null)
        {
            return authResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        dto.StaffActor = ResolveStaffActor() ?? dto.StaffActor; // actor from the JWT, not the body

        try
        {
            var result = await _documentService.VerifyDocumentAsync(engagementId, documentId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Document '{documentId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Document verification precondition failed for document {DocumentId}", documentId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Staff rejection endpoint: Manually rejects document verification with a mandatory reason.
    /// Restricted to authorized staff (Owner, Staff).
    /// Precondition: Document must already be automatically compliant.
    /// </summary>
    [HttpPost("{documentId:guid}/reject")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<DocumentResponseDto>> RejectDocument(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromBody] RejectDocumentRequestDto dto,
        [FromQuery] string? tenantId)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var authResult = CheckStaffAuthorization();
        if (authResult != null)
        {
            return authResult;
        }

        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            return BadRequest(new { message = "Rejection reason is required." });
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        dto.StaffActor = ResolveStaffActor() ?? dto.StaffActor; // actor from the JWT, not the body

        try
        {
            var result = await _documentService.RejectDocumentVerificationAsync(engagementId, documentId, effectiveTenantId, dto);
            if (result == null)
            {
                return NotFound(new { message = $"Document '{documentId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Document verification precondition failed for document {DocumentId}", documentId);
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Staff metadata update endpoint: Updates editable metadata fields for a specific document without silently rerunning validation.
    /// Restricted to authorized staff (Owner, Staff).
    /// </summary>
    [HttpPut("{documentId:guid}/metadata")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<DocumentResponseDto>> UpdateDocumentMetadata(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromBody] UpdateDocumentMetadataDto dto,
        [FromQuery] string? tenantId = null)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var authResult = CheckStaffAuthorization();
        if (authResult != null)
        {
            return authResult;
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var staffActor = ResolveStaffActor();

        try
        {
            var result = await _documentService.UpdateDocumentMetadataAsync(engagementId, documentId, effectiveTenantId, dto, staffActor);
            if (result == null)
            {
                return NotFound(new { message = $"Document '{documentId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Document metadata update failed for document {DocumentId}", documentId);
            return BadRequest(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Staff soft-delete endpoint: Soft-deletes a document, retaining physical storage file and historical evidence.
    /// Restricted to authorized staff (Owner, Staff).
    /// </summary>
    [HttpDelete("{documentId:guid}")]
    [Authorize(Roles = "Owner,Staff")]
    public async Task<ActionResult<DocumentResponseDto>> SoftDeleteDocument(
        [FromRoute] Guid engagementId,
        [FromRoute] Guid documentId,
        [FromQuery] string? tenantId = null)
    {
        var (effectiveTenantId, isForbidden) = TryResolveTenantId(tenantId);
        if (isForbidden)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(effectiveTenantId))
        {
            return BadRequest(new { message = "Tenant identification is required via JWT claim, X-Tenant-ID header, or tenantId parameter." });
        }

        var authResult = CheckStaffAuthorization();
        if (authResult != null)
        {
            return authResult;
        }

        var staffActor = ResolveStaffActor();

        try
        {
            var result = await _documentService.SoftDeleteDocumentAsync(engagementId, documentId, effectiveTenantId, staffActor);
            if (result == null)
            {
                return NotFound(new { message = $"Document '{documentId}' was not found for engagement '{engagementId}' and tenant '{effectiveTenantId}'." });
            }

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Document soft-delete failed for document {DocumentId}", documentId);
            return BadRequest(new { message = ex.Message });
        }
    }

    private ActionResult? CheckStaffAuthorization()
    {
        // 1. If ClaimsPrincipal is authenticated, check role claims
        if (User?.Identity?.IsAuthenticated == true)
        {
            var isStaff = User.IsInRole("Owner") || User.IsInRole("Staff") ||
                          User.Claims.Any(c => c.Type == System.Security.Claims.ClaimTypes.Role &&
                              (c.Value.Equals("Owner", StringComparison.OrdinalIgnoreCase) || c.Value.Equals("Staff", StringComparison.OrdinalIgnoreCase)));

            return isStaff ? null : Forbid();
        }


        // 3. Neither authenticated claim nor valid role header found
        return Unauthorized(new { message = "Authentication required. Only authorized staff can verify or reject documents." });
    }

    /// <summary>
    /// A Client may only touch documents of an engagement it owns (ownership is Workflow's). Owner and
    /// Staff act across their tenant. Returns 403 when denied and 503 when Workflow cannot confirm (fail closed).
    /// </summary>
    private async Task<ActionResult?> EnsureClientCanAccessEngagementAsync(Guid engagementId, string tenantId)
    {
        if (User?.IsInRole("Client") != true)
        {
            return null;
        }

        try
        {
            var allowed = await _engagementAccess.CanAccessEngagementAsync(engagementId, tenantId, HttpContext?.RequestAborted ?? default);
            return allowed ? null : Forbid();
        }
        catch (EngagementAccessUnavailableException ex)
        {
            _logger.LogWarning(ex, "Could not confirm client access to engagement {EngagementId}", engagementId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Unable to confirm access to this engagement right now. Please try again shortly." });
        }
    }

    private string? ResolveStaffActor()
    {
        var actor = User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User?.FindFirst("sub")?.Value
            ?? User?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
            ?? User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        if (!string.IsNullOrWhiteSpace(actor))
        {
            return actor.Trim();
        }


        return "StaffUser";
    }

    /// <summary>
    /// Resolves tenant ID server-side from HttpContext JWT claims if authenticated.
    /// Strictly rejects cross-tenant requests where a caller specifies a different tenant ID than their JWT claim.
    /// Falls back to request header/query parameter only in unauthenticated test mock contexts.
    /// </summary>
    private (string? TenantId, bool IsForbidden) TryResolveTenantId(string? queryTenantId)
    {
        var jwtClaimTenant = User?.FindFirst("tenant_id")?.Value ?? User?.FindFirst("tenantId")?.Value;
        if (!string.IsNullOrWhiteSpace(jwtClaimTenant))
        {
            var cleanJwtTenant = jwtClaimTenant.Trim();

            // Check if query tenant parameter conflicts
            if (!string.IsNullOrWhiteSpace(queryTenantId) &&
                !string.Equals(queryTenantId.Trim(), cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
            {
                return (null, true);
            }

            // Check if header tenant conflicts
            if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerVal))
            {
                var headerTenant = headerVal.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(headerTenant) &&
                    !string.Equals(headerTenant, cleanJwtTenant, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, true);
                }
            }

            return (cleanJwtTenant, false);
        }

        // 1. Check HTTP header X-Tenant-ID (for unauthenticated test contexts)
        if (Request?.Headers != null && Request.Headers.TryGetValue("X-Tenant-ID", out var headerValue))
        {
            var headerTenant = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(headerTenant))
            {
                return (headerTenant.Trim(), false);
            }
        }

        // 2. Fallback to Query String Parameter
        if (!string.IsNullOrWhiteSpace(queryTenantId))
        {
            return (queryTenantId.Trim(), false);
        }

        return (null, false);
    }
}
