using Custodian.Documents.Data;
using Custodian.Documents.DTOs;
using Custodian.Documents.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Documents.Services;

public class DocumentService : IDocumentService
{
    private readonly DocumentDbContext _dbContext;
    private readonly IDocumentValidator _validator;
    private readonly IStorageService _storageService;

    public DocumentService(
        DocumentDbContext dbContext,
        IDocumentValidator validator,
        IStorageService storageService)
    {
        _dbContext = dbContext;
        _validator = validator;
        _storageService = storageService;
    }

    public async Task<DocumentResponseDto> UploadDocumentAsync(Guid engagementId, string tenantId, DocumentUploadDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        }

        var validationResult = _validator.Validate(dto.File);
        if (!validationResult.IsValid)
        {
            throw new ArgumentException(validationResult.ErrorMessage ?? "Invalid file.", nameof(dto));
        }

        var documentId = Guid.NewGuid();
        var storagePath = await _storageService.SaveFileAsync(dto.File, tenantId, engagementId, documentId);

        var metadata = new DocumentMetadata
        {
            DocumentId = documentId,
            EngagementId = engagementId,
            TenantId = tenantId,
            Type = dto.Type,
            IssueDate = dto.IssueDate,
            ExpiryDate = dto.ExpiryDate,
            UploaderId = dto.UploaderId,
            FileName = dto.File.FileName,
            ContentType = dto.File.ContentType,
            FileSize = dto.File.Length,
            StoragePath = storagePath,
            UploadedAt = DateTime.UtcNow
        };

        _dbContext.Documents.Add(metadata);
        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(metadata);
    }

    public async Task<IEnumerable<DocumentResponseDto>> GetDocumentsByEngagementAsync(Guid engagementId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Enumerable.Empty<DocumentResponseDto>();
        }

        var documents = await _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.EngagementId == engagementId && d.TenantId == tenantId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        return documents.Select(MapToResponseDto);
    }

    public async Task<DocumentResponseDto?> GetDocumentByIdAsync(Guid engagementId, Guid documentId, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var document = await _dbContext.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.EngagementId == engagementId && d.TenantId == tenantId);

        return document == null ? null : MapToResponseDto(document);
    }

    private static DocumentResponseDto MapToResponseDto(DocumentMetadata entity)
    {
        return new DocumentResponseDto
        {
            DocumentId = entity.DocumentId,
            EngagementId = entity.EngagementId,
            TenantId = entity.TenantId,
            Type = entity.Type,
            IssueDate = entity.IssueDate,
            ExpiryDate = entity.ExpiryDate,
            UploaderId = entity.UploaderId,
            FileName = entity.FileName,
            ContentType = entity.ContentType,
            FileSize = entity.FileSize,
            StoragePath = entity.StoragePath,
            UploadedAt = entity.UploadedAt
        };
    }
}
