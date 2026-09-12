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
            VerificationReason = null
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
            VerificationReason = entity.VerificationReason
        };
    }
}
