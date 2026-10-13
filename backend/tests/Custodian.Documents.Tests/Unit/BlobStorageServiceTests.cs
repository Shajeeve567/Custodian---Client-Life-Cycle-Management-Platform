using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Custodian.Documents.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class BlobStorageServiceTests
{
    private readonly Mock<BlobContainerClient> _containerMock = new();
    private readonly Mock<BlobClient> _blobMock = new();
    private readonly BlobStorageService _storageService;

    public BlobStorageServiceTests()
    {
        _containerMock.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(_blobMock.Object);
        _storageService = new BlobStorageService(_containerMock.Object);
    }

    private static IFormFile CreateTestFormFile(byte[] content)
    {
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
        return fileMock.Object;
    }

    [Fact]
    public async Task SaveFileAsync_UploadsPdfUnderTenantAndEngagement_AndReturnsBlobName()
    {
        var tenantId = "tenant-100";
        var engagementId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var fileContent = Encoding.UTF8.GetBytes("%PDF-1.4 test document content");
        byte[]? uploaded = null;
        BlobUploadOptions? uploadOptions = null;
        _blobMock
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Callback((Stream s, BlobUploadOptions o, CancellationToken _) =>
            {
                using var copy = new MemoryStream();
                s.CopyTo(copy);
                uploaded = copy.ToArray();
                uploadOptions = o;
            })
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        var storagePath = await _storageService.SaveFileAsync(CreateTestFormFile(fileContent), tenantId, engagementId, documentId);

        var expectedName = $"{tenantId}/{engagementId}/{documentId}.pdf";
        Assert.Equal(expectedName, storagePath);
        _containerMock.Verify(c => c.GetBlobClient(expectedName), Times.Once);
        Assert.Equal(fileContent, uploaded);
        Assert.Equal("application/pdf", uploadOptions?.HttpHeaders?.ContentType);
    }

    [Fact]
    public async Task SaveFileAsync_WithEmptyTenant_Throws()
    {
        var file = CreateTestFormFile(Encoding.UTF8.GetBytes("%PDF"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storageService.SaveFileAsync(file, " ", Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task GetFileAsync_ExistingBlob_ReturnsItsContent()
    {
        var fileContent = Encoding.UTF8.GetBytes("%PDF-1.4 stored content");
        var result = BlobsModelFactory.BlobDownloadStreamingResult(content: new MemoryStream(fileContent));
        _blobMock
            .Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));

        using var stream = await _storageService.GetFileAsync("tenant-100/eng/doc.pdf");

        Assert.NotNull(stream);
        using var reader = new MemoryStream();
        await stream!.CopyToAsync(reader);
        Assert.Equal(fileContent, reader.ToArray());
        _containerMock.Verify(c => c.GetBlobClient("tenant-100/eng/doc.pdf"), Times.Once);
    }

    [Fact]
    public async Task GetFileAsync_MissingBlob_ReturnsNull()
    {
        _blobMock
            .Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "BlobNotFound"));

        var stream = await _storageService.GetFileAsync("tenant-100/eng/missing.pdf");

        Assert.Null(stream);
    }

    [Fact]
    public async Task GetFileAsync_OtherStorageFailure_IsNotSwallowed()
    {
        _blobMock
            .Setup(b => b.DownloadStreamingAsync(It.IsAny<BlobDownloadOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "AuthenticationFailed"));

        await Assert.ThrowsAsync<RequestFailedException>(() => _storageService.GetFileAsync("tenant-100/eng/doc.pdf"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetAndDelete_WithEmptyPath_DoNotCallStorage(string? storagePath)
    {
        Assert.Null(await _storageService.GetFileAsync(storagePath!));
        Assert.False(await _storageService.DeleteFileAsync(storagePath!));
        _containerMock.Verify(c => c.GetBlobClient(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeleteFileAsync_ReturnsWhetherTheBlobExisted(bool existed)
    {
        _blobMock
            .Setup(b => b.DeleteIfExistsAsync(It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existed, Mock.Of<Response>()));

        var deleted = await _storageService.DeleteFileAsync("tenant-100/eng/doc.pdf");

        Assert.Equal(existed, deleted);
    }
}
