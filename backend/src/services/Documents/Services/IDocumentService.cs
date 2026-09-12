using Custodian.Documents.DTOs;

namespace Custodian.Documents.Services;

public interface IDocumentService
{
    Task<DocumentResponseDto> UploadDocumentAsync(Guid engagementId, string tenantId, DocumentUploadDto dto);
    Task<IEnumerable<DocumentResponseDto>> GetDocumentsByEngagementAsync(Guid engagementId, string tenantId);
    Task<DocumentResponseDto?> GetDocumentByIdAsync(Guid engagementId, Guid documentId, string tenantId);
    Task<DocumentResponseDto?> VerifyDocumentAsync(Guid engagementId, Guid documentId, string tenantId, VerifyDocumentRequestDto dto);
    Task<DocumentResponseDto?> RejectDocumentVerificationAsync(Guid engagementId, Guid documentId, string tenantId, RejectDocumentRequestDto dto);
}
