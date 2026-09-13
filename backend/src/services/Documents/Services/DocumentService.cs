using Custodian.Documents.Compliance;
using Custodian.Documents.Compliance.Rules;
using Custodian.Documents.Compliance.Store;
using Custodian.Documents.Data;
using Custodian.Documents.DTOs;
using Custodian.Documents.Models;
using Custodian.Shared.Contracts;
using Custodian.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Documents.Services;

public class DocumentService : IDocumentService
{
    private readonly DocumentDbContext _dbContext;
    private readonly IDocumentValidator _validator;
    private readonly IStorageService _storageService;
    private readonly IComplianceRuleEngine _complianceEngine;
    private readonly IAuditPublisher? _auditPublisher;

    public DocumentService(
        DocumentDbContext dbContext,
        IDocumentValidator validator,
        IStorageService storageService,
        IComplianceRuleEngine? complianceEngine = null,
        IAuditPublisher? auditPublisher = null)
    {
        _dbContext = dbContext;
        _validator = validator;
        _storageService = storageService;
        _complianceEngine = complianceEngine ?? new ComplianceRuleEngine(
            new IComplianceRule[] { new DocumentFreshnessRule(), new DocumentExpiryRule() },
            new ComplianceRuleStore());
        _auditPublisher = auditPublisher;
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

        // Execute deterministic compliance validation automatically on upload
        var complianceResult = _complianceEngine.Evaluate(
            documentType: dto.Type,
            issueDate: dto.IssueDate,
            expiryDate: dto.ExpiryDate,
            ruleDefinition: null,
            fileName: dto.File.FileName);

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
            UploadedAt = DateTime.UtcNow,
            ComplianceStatus = complianceResult.Status,
            RejectionReason = complianceResult.RejectionReason,
            ValidatedAt = complianceResult.ValidatedAtUtc,
            VerificationStatus = DocumentVerificationStatus.Unverified,
            VerifiedBy = null,
            VerifiedAt = null,
            VerificationReason = null,
            IsDeleted = false,
            DeletedAt = null,
            DeletedBy = null
        };

        _dbContext.Documents.Add(metadata);
        await _dbContext.SaveChangesAsync();

        return MapToResponseDto(metadata);
    }

    public Task<IEnumerable<DocumentResponseDto>> GetDocumentsByEngagementAsync(Guid engagementId, string tenantId)
    {
        return GetDocumentsByEngagementAsync(engagementId, tenantId, filter: null);
    }

    public async Task<IEnumerable<DocumentResponseDto>> GetDocumentsByEngagementAsync(
        Guid engagementId,
        string tenantId,
        DocumentFilterDto? filter)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Enumerable.Empty<DocumentResponseDto>();
        }

        var query = _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.EngagementId == engagementId && d.TenantId == tenantId);

        if (filter != null)
        {
            if (!filter.IncludeDeleted)
            {
                query = query.Where(d => !d.IsDeleted);
            }

            if (!string.IsNullOrWhiteSpace(filter.Type))
            {
                var type = filter.Type.Trim();
                query = query.Where(d => d.Type == type);
            }

            if (!string.IsNullOrWhiteSpace(filter.ComplianceStatus))
            {
                var compStatus = filter.ComplianceStatus.Trim();
                query = query.Where(d => d.ComplianceStatus == compStatus);
            }

            if (!string.IsNullOrWhiteSpace(filter.VerificationStatus))
            {
                var verStatus = filter.VerificationStatus.Trim();
                query = query.Where(d => d.VerificationStatus == verStatus);
            }

            if (!string.IsNullOrWhiteSpace(filter.UploaderId))
            {
                var uploaderId = filter.UploaderId.Trim();
                query = query.Where(d => d.UploaderId == uploaderId);
            }
        }
        else
        {
            query = query.Where(d => !d.IsDeleted);
        }

        var documents = await query
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();

        return documents.Select(MapToResponseDto);
    }

    public Task<DocumentResponseDto?> GetDocumentByIdAsync(Guid engagementId, Guid documentId, string tenantId)
    {
        return GetDocumentByIdAsync(engagementId, documentId, tenantId, includeDeleted: false);
    }

    public async Task<DocumentResponseDto?> GetDocumentByIdAsync(
        Guid engagementId,
        Guid documentId,
        string tenantId,
        bool includeDeleted)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var query = _dbContext.Documents
            .AsNoTracking()
            .Where(d => d.DocumentId == documentId && d.EngagementId == engagementId && d.TenantId == tenantId);

        if (!includeDeleted)
        {
            query = query.Where(d => !d.IsDeleted);
        }

        var document = await query.FirstOrDefaultAsync();

        return document == null ? null : MapToResponseDto(document);
    }

    public async Task<DocumentResponseDto?> VerifyDocumentAsync(
        Guid engagementId,
        Guid documentId,
        string tenantId,
        VerifyDocumentRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.EngagementId == engagementId && d.TenantId == tenantId);

        if (document == null)
        {
            return null;
        }

        // AC 2: Only auto-compliant documents enter verification
        if (!string.Equals(document.ComplianceStatus, Compliance.ComplianceStatus.Compliant, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Document '{documentId}' is not automatically compliant (Compliance Status: '{document.ComplianceStatus}'). Only automatically compliant documents can enter human verification.");
        }

        // AC 4: Verification separate from compliance (compliance status remains compliant)
        document.VerificationStatus = DocumentVerificationStatus.Verified;
        document.VerifiedBy = !string.IsNullOrWhiteSpace(dto.StaffActor) ? dto.StaffActor.Trim() : "StaffUser";
        document.VerifiedAt = DateTime.UtcNow;
        document.VerificationReason = dto.StaffNotes?.Trim();

        await _dbContext.SaveChangesAsync();

        if (_auditPublisher != null)
        {
            try
            {
                var auditPayload = new
                {
                    documentId = document.DocumentId,
                    engagementId = document.EngagementId,
                    tenantId = document.TenantId,
                    documentType = document.Type,
                    complianceStatus = document.ComplianceStatus,
                    verificationStatus = document.VerificationStatus,
                    verifiedBy = document.VerifiedBy,
                    verifiedAtUtc = document.VerifiedAt,
                    notes = document.VerificationReason
                };

                await _auditPublisher.PublishEventAsync(
                    engagementId,
                    tenantId,
                    document.VerifiedBy ?? "StaffUser",
                    EventTypes.DocumentVerified,
                    auditPayload);
            }
            catch
            {
                // Non-blocking: audit event side effects must not silently corrupt or fail the primary transaction
            }
        }

        return MapToResponseDto(document);
    }

    public async Task<DocumentResponseDto?> RejectDocumentVerificationAsync(
        Guid engagementId,
        Guid documentId,
        string tenantId,
        RejectDocumentRequestDto dto)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(dto.Reason))
        {
            throw new ArgumentException("Rejection reason is required when rejecting document verification.", nameof(dto));
        }

        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.EngagementId == engagementId && d.TenantId == tenantId);

        if (document == null)
        {
            return null;
        }

        // AC 2: Only auto-compliant documents enter verification
        if (!string.Equals(document.ComplianceStatus, Compliance.ComplianceStatus.Compliant, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Document '{documentId}' is not automatically compliant (Compliance Status: '{document.ComplianceStatus}'). Only automatically compliant documents can enter human verification.");
        }

        // AC 3 & AC 4: Verification separate from compliance (compliance status remains compliant, rejection reason recorded in verification reason)
        document.VerificationStatus = DocumentVerificationStatus.Rejected;
        document.VerifiedBy = !string.IsNullOrWhiteSpace(dto.StaffActor) ? dto.StaffActor.Trim() : "StaffUser";
        document.VerifiedAt = DateTime.UtcNow;
        document.VerificationReason = dto.Reason.Trim();

        await _dbContext.SaveChangesAsync();

        if (_auditPublisher != null)
        {
            try
            {
                var auditPayload = new
                {
                    documentId = document.DocumentId,
                    engagementId = document.EngagementId,
                    tenantId = document.TenantId,
                    documentType = document.Type,
                    complianceStatus = document.ComplianceStatus,
                    verificationStatus = document.VerificationStatus,
                    verifiedBy = document.VerifiedBy,
                    verifiedAtUtc = document.VerifiedAt,
                    rejectionReason = document.VerificationReason
                };

                await _auditPublisher.PublishEventAsync(
                    engagementId,
                    tenantId,
                    document.VerifiedBy ?? "StaffUser",
                    EventTypes.DocumentVerificationRejected,
                    auditPayload);
            }
            catch
            {
                // Non-blocking: audit event side effects must not silently corrupt or fail the primary transaction
            }
        }

        return MapToResponseDto(document);
    }

    public Task<DocumentResponseDto?> UpdateDocumentMetadataAsync(
        Guid engagementId,
        Guid documentId,
        string tenantId,
        UpdateDocumentMetadataDto dto) =>
        UpdateDocumentMetadataAsync(engagementId, documentId, tenantId, dto, staffActor: null);

    public async Task<DocumentResponseDto?> UpdateDocumentMetadataAsync(
        Guid engagementId,
        Guid documentId,
        string tenantId,
        UpdateDocumentMetadataDto dto,
        string? staffActor)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.EngagementId == engagementId && d.TenantId == tenantId);

        if (document == null)
        {
            return null;
        }

        if (document.IsDeleted)
        {
            throw new InvalidOperationException($"Cannot update metadata for soft-deleted document '{documentId}'.");
        }

        if (dto.IssueDate.HasValue && dto.ExpiryDate.HasValue && dto.ExpiryDate < dto.IssueDate)
        {
            throw new ArgumentException("ExpiryDate cannot be earlier than IssueDate.");
        }

        if (!string.IsNullOrWhiteSpace(dto.Type))
        {
            document.Type = dto.Type.Trim();
        }

        if (dto.IssueDate.HasValue)
        {
            document.IssueDate = dto.IssueDate;
        }

        if (dto.ExpiryDate.HasValue)
        {
            document.ExpiryDate = dto.ExpiryDate;
        }

        // AC: Metadata update does not silently rerun validation.
        // ComplianceRuleEngine is intentionally NOT called,
        // and existing ComplianceStatus, ValidatedAt, RejectionReason, and verification states are preserved.

        await _dbContext.SaveChangesAsync();

        if (_auditPublisher != null)
        {
            try
            {
                var auditPayload = new
                {
                    documentId = document.DocumentId,
                    engagementId = document.EngagementId,
                    tenantId = document.TenantId,
                    documentType = document.Type,
                    issueDate = document.IssueDate,
                    expiryDate = document.ExpiryDate,
                    updatedBy = !string.IsNullOrWhiteSpace(staffActor) ? staffActor.Trim() : "StaffUser",
                    updatedAtUtc = DateTime.UtcNow
                };

                await _auditPublisher.PublishEventAsync(
                    engagementId,
                    tenantId,
                    !string.IsNullOrWhiteSpace(staffActor) ? staffActor.Trim() : "StaffUser",
                    EventTypes.DocumentMetadataUpdated,
                    auditPayload);
            }
            catch
            {
                // Non-blocking: audit event side effects must not silently corrupt or fail the primary transaction
            }
        }

        return MapToResponseDto(document);
    }

    public async Task<DocumentResponseDto?> SoftDeleteDocumentAsync(
        Guid engagementId,
        Guid documentId,
        string tenantId,
        string? staffActor)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return null;
        }

        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.EngagementId == engagementId && d.TenantId == tenantId);

        if (document == null)
        {
            return null;
        }

        if (document.IsDeleted)
        {
            throw new InvalidOperationException($"Document '{documentId}' is already deleted.");
        }

        document.IsDeleted = true;
        document.DeletedAt = DateTime.UtcNow;
        document.DeletedBy = !string.IsNullOrWhiteSpace(staffActor) ? staffActor.Trim() : "StaffUser";

        // AC: Soft delete retains history/file per MVP.
        // Storage file is explicitly NOT deleted to preserve evidentiary history.
        // DbContext document entity is explicitly NOT removed.

        await _dbContext.SaveChangesAsync();

        if (_auditPublisher != null)
        {
            try
            {
                var auditPayload = new
                {
                    documentId = document.DocumentId,
                    engagementId = document.EngagementId,
                    tenantId = document.TenantId,
                    documentType = document.Type,
                    fileName = document.FileName,
                    storagePath = document.StoragePath,
                    deletedBy = document.DeletedBy,
                    deletedAtUtc = document.DeletedAt
                };

                await _auditPublisher.PublishEventAsync(
                    engagementId,
                    tenantId,
                    document.DeletedBy ?? "StaffUser",
                    EventTypes.DocumentSoftDeleted,
                    auditPayload);
            }
            catch
            {
                // Non-blocking: audit event side effects must not silently corrupt or fail the primary transaction
            }
        }

        return MapToResponseDto(document);
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
            UploadedAt = entity.UploadedAt,
            ComplianceStatus = entity.ComplianceStatus,
            RejectionReason = entity.RejectionReason,
            ValidatedAt = entity.ValidatedAt,
            VerificationStatus = entity.VerificationStatus,
            VerifiedBy = entity.VerifiedBy,
            VerifiedAt = entity.VerifiedAt,
            VerificationReason = entity.VerificationReason,
            IsDeleted = entity.IsDeleted,
            DeletedAt = entity.DeletedAt,
            DeletedBy = entity.DeletedBy
        };
    }
}
