using Azure.Storage.Blobs;
using Custodian.Documents.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class StorageServiceRegistrationTests
{
    // Parsed locally only; no request is made when the client is constructed.
    private const string DevStorageConnection = "UseDevelopmentStorage=true";

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDocumentStorage(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void NoProvider_UsesLocalStorage()
    {
        using var provider = Build(new());
        using var scope = provider.CreateScope();

        Assert.IsType<LocalStorageService>(scope.ServiceProvider.GetRequiredService<IStorageService>());
    }

    [Fact]
    public void BlobProvider_UsesBlobStorage_WithDefaultContainer()
    {
        using var provider = Build(new()
        {
            ["Storage:Provider"] = "blob",
            ["AzureBlob:ConnectionString"] = DevStorageConnection
        });
        using var scope = provider.CreateScope();

        Assert.IsType<BlobStorageService>(scope.ServiceProvider.GetRequiredService<IStorageService>());
        Assert.Equal(StorageServiceRegistration.DefaultContainerName, provider.GetRequiredService<BlobContainerClient>().Name);
    }

    [Fact]
    public void BlobProvider_UsesConfiguredContainer()
    {
        using var provider = Build(new()
        {
            ["Storage:Provider"] = "Blob",
            ["AzureBlob:ConnectionString"] = DevStorageConnection,
            ["AzureBlob:ContainerName"] = "staging-documents"
        });

        Assert.Equal("staging-documents", provider.GetRequiredService<BlobContainerClient>().Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FILL_IN_HERE")]
    public void BlobProvider_WithoutConnectionString_FailsAtStartup(string? connectionString)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Storage:Provider"] = "Blob",
            ["AzureBlob:ConnectionString"] = connectionString
        }));

        Assert.Contains("AzureBlob:ConnectionString", ex.Message);
    }
}
