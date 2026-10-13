using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Http;

namespace Custodian.Documents.Services;

/// <summary>
/// Stores document binaries in an Azure Blob Storage container. The storage path saved in the database
/// is the blob name ({tenantId}/{engagementId}/{documentId}.pdf), so every instance of the service reads
/// the same files and deploys never wipe them (unlike LocalStorageService on App Service).
/// </summary>
public class BlobStorageService : IStorageService
{
    private readonly BlobContainerClient _container;

    public BlobStorageService(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task<string> SaveFileAsync(IFormFile file, string tenantId, Guid engagementId, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));

        var blobName = $"{tenantId}/{engagementId}/{documentId}.pdf";
        var blob = _container.GetBlobClient(blobName);

        await using var content = file.OpenReadStream();
        await blob.UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
        });

        return blobName;
    }

    public async Task<Stream?> GetFileAsync(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return null;
        }

        try
        {
            var download = await _container.GetBlobClient(storagePath).DownloadStreamingAsync();
            return download.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteFileAsync(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return false;
        }

        var response = await _container.GetBlobClient(storagePath).DeleteIfExistsAsync();
        return response.Value;
    }
}
