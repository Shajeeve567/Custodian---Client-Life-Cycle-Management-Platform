using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Custodian.Documents.Services;

public class LocalStorageService : IStorageService
{
    private readonly string _rootUploadPath;

    public LocalStorageService(IConfiguration configuration)
    {
        var configuredPath = configuration["Storage:UploadPath"];
        _rootUploadPath = string.IsNullOrWhiteSpace(configuredPath) ? "uploads" : configuredPath;
    }

    public LocalStorageService(string rootUploadPath)
    {
        _rootUploadPath = rootUploadPath;
    }

    public async Task<string> SaveFileAsync(IFormFile file, string tenantId, Guid engagementId, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));

        var relativeDirectory = Path.Combine(_rootUploadPath, tenantId, engagementId.ToString());
        var absoluteDirectory = Path.GetFullPath(relativeDirectory);

        if (!Directory.Exists(absoluteDirectory))
        {
            Directory.CreateDirectory(absoluteDirectory);
        }

        var fileName = $"{documentId}.pdf";
        var absoluteFilePath = Path.Combine(absoluteDirectory, fileName);

        using (var stream = new FileStream(absoluteFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(stream);
        }

        // Normalize relative storage path with forward slashes for database consistency
        var relativePath = Path.Combine(_rootUploadPath, tenantId, engagementId.ToString(), fileName).Replace('\\', '/');
        return relativePath;
    }

    public Task<Stream?> GetFileAsync(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        var absolutePath = Path.GetFullPath(storagePath);
        if (!File.Exists(absolutePath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> DeleteFileAsync(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return Task.FromResult(false);
        }

        var absolutePath = Path.GetFullPath(storagePath);
        if (!File.Exists(absolutePath))
        {
            return Task.FromResult(false);
        }

        File.Delete(absolutePath);
        return Task.FromResult(true);
    }
}
