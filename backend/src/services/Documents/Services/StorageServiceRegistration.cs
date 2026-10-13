using Azure.Storage.Blobs;

namespace Custodian.Documents.Services;

public static class StorageServiceRegistration
{
    public const string DefaultContainerName = "custodian-documents";

    /// <summary>
    /// Registers the document binary store selected by 'Storage:Provider':
    /// - "Blob": Azure Blob Storage, using 'AzureBlob:ConnectionString' and 'AzureBlob:ContainerName'
    ///   (default custodian-documents). The container must already exist. Fails at startup when the
    ///   connection string is missing, rather than on the first upload.
    /// - anything else (default "Local"): files on disk under 'Storage:UploadPath' (LocalStorageService).
    /// </summary>
    public static IServiceCollection AddDocumentStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"];
        if (!string.Equals(provider, "Blob", StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<IStorageService, LocalStorageService>();
            return services;
        }

        var connectionString = configuration["AzureBlob:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("FILL_IN_HERE"))
        {
            throw new InvalidOperationException(
                "Storage:Provider is 'Blob' but AzureBlob:ConnectionString is not set.");
        }

        var containerName = configuration["AzureBlob:ContainerName"];
        if (string.IsNullOrWhiteSpace(containerName))
        {
            containerName = DefaultContainerName;
        }

        // BlobContainerClient is thread-safe and meant to be reused for the app's lifetime.
        services.AddSingleton(new BlobContainerClient(connectionString, containerName));
        services.AddScoped<IStorageService, BlobStorageService>();
        return services;
    }
}
