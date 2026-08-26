using Microsoft.AspNetCore.Http;

namespace Custodian.Documents.Services;

public interface IStorageService
{
    Task<string> SaveFileAsync(IFormFile file, string tenantId, Guid engagementId, Guid documentId);
    Task<Stream?> GetFileAsync(string storagePath);
    Task<bool> DeleteFileAsync(string storagePath);
}
