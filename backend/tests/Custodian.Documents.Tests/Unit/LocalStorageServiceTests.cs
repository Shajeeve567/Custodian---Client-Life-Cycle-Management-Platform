using System.Text;
using Custodian.Documents.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class LocalStorageServiceTests : IDisposable
{
    private readonly string _tempTestDir;
    private readonly LocalStorageService _storageService;

    public LocalStorageServiceTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "CustodianStorageTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
        _storageService = new LocalStorageService(_tempTestDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempTestDir))
        {
            try
            {
                Directory.Delete(_tempTestDir, true);
            }
            catch
            {
                // Ignore cleanup errors in temp dir
            }
        }
    }

    private static IFormFile CreateTestFormFile(byte[] content)
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
        fileMock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns((Stream targetStream, CancellationToken ct) => targetStream.WriteAsync(content, 0, content.Length, ct));
        return fileMock.Object;
    }

    [Fact]
    public async Task SaveFileAsync_CreatesTenantAndEngagementDirectory_AndSavesFile()
    {
        var tenantId = "tenant-100";
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var fileContent = Encoding.UTF8.GetBytes("%PDF-1.4 test document content");
        var formFile = CreateTestFormFile(fileContent);

        var relativePath = await _storageService.SaveFileAsync(formFile, tenantId, engagementId, documentId);

        Assert.NotNull(relativePath);
        Assert.Contains(tenantId, relativePath);
        Assert.Contains(engagementId.ToString(), relativePath);
        Assert.EndsWith($"{documentId}.pdf", relativePath);

        var absoluteSavedPath = Path.GetFullPath(relativePath);
        Assert.True(File.Exists(absoluteSavedPath));

        var savedBytes = await File.ReadAllBytesAsync(absoluteSavedPath);
        Assert.Equal(fileContent, savedBytes);
    }

    [Fact]
    public async Task GetFileAsync_ReturnsFileStream_WhenFileExists()
    {
        var tenantId = "tenant-101";
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var fileContent = Encoding.UTF8.GetBytes("%PDF-1.4 sample stream");
        var formFile = CreateTestFormFile(fileContent);

        var storagePath = await _storageService.SaveFileAsync(formFile, tenantId, engagementId, documentId);

        using var stream = await _storageService.GetFileAsync(storagePath);

        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var readContent = await reader.ReadToEndAsync();
        Assert.Equal("%PDF-1.4 sample stream", readContent);
    }

    [Fact]
    public async Task GetFileAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        var nonExistentPath = Path.Combine(_tempTestDir, "nonexistent", "doc.pdf");
        var stream = await _storageService.GetFileAsync(nonExistentPath);

        Assert.Null(stream);
    }

    [Fact]
    public async Task DeleteFileAsync_DeletesFile_WhenFileExists()
    {
        var tenantId = "tenant-102";
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var fileContent = Encoding.UTF8.GetBytes("%PDF-1.4 document to delete");
        var formFile = CreateTestFormFile(fileContent);

        var storagePath = await _storageService.SaveFileAsync(formFile, tenantId, engagementId, documentId);
        var absolutePath = Path.GetFullPath(storagePath);
        Assert.True(File.Exists(absolutePath));

        var deleted = await _storageService.DeleteFileAsync(storagePath);

        Assert.True(deleted);
        Assert.False(File.Exists(absolutePath));
    }
}
