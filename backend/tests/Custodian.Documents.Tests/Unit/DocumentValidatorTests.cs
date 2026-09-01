using System.Text;
using Custodian.Documents.Services;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class DocumentValidatorTests
{
    private readonly DocumentValidator _validator = new();

    private static IFormFile CreateMockFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        var fileMock = new Mock<IFormFile>();
        fileMock.Setup(f => f.FileName).Returns(fileName);
        fileMock.Setup(f => f.ContentType).Returns(contentType);
        fileMock.Setup(f => f.Length).Returns(content.Length);
        fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
        return fileMock.Object;
    }

    private static byte[] CreatePdfContent(int length = 100)
    {
        var header = Encoding.UTF8.GetBytes("%PDF-1.4 sample pdf content");
        var content = new byte[Math.Max(length, header.Length)];
        Array.Copy(header, content, header.Length);
        return content;
    }

    [Fact]
    public void Validate_NullFile_ReturnsFailure()
    {
        var result = _validator.Validate(null);

        Assert.False(result.IsValid);
        Assert.Equal("File is required and cannot be empty.", result.ErrorMessage);
    }

    [Fact]
    public void Validate_EmptyFile_ReturnsFailure()
    {
        var file = CreateMockFormFile("test.pdf", "application/pdf", Array.Empty<byte>());

        var result = _validator.Validate(file);

        Assert.False(result.IsValid);
        Assert.Equal("File is required and cannot be empty.", result.ErrorMessage);
    }

    [Fact]
    public void Validate_FileExceeding5MB_ReturnsFailure()
    {
        var largeContent = new byte[DocumentValidator.MaxFileSizeBytes + 1];
        var header = Encoding.UTF8.GetBytes("%PDF-");
        Array.Copy(header, largeContent, header.Length);

        var file = CreateMockFormFile("large.pdf", "application/pdf", largeContent);

        var result = _validator.Validate(file);

        Assert.False(result.IsValid);
        Assert.Equal("File size exceeds maximum limit of 5 MB.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("document.txt")]
    [InlineData("image.jpg")]
    [InlineData("script.exe")]
    [InlineData("pdf_without_ext")]
    public void Validate_NonPdfExtension_ReturnsFailure(string fileName)
    {
        var pdfContent = CreatePdfContent();
        var file = CreateMockFormFile(fileName, "application/pdf", pdfContent);

        var result = _validator.Validate(file);

        Assert.False(result.IsValid);
        Assert.Equal("Only PDF files are allowed.", result.ErrorMessage);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    public void Validate_NonPdfContentType_ReturnsFailure(string contentType)
    {
        var pdfContent = CreatePdfContent();
        var file = CreateMockFormFile("test.pdf", contentType, pdfContent);

        var result = _validator.Validate(file);

        Assert.False(result.IsValid);
        Assert.Equal("Only PDF files are allowed.", result.ErrorMessage);
    }

    [Fact]
    public void Validate_InvalidMagicBytes_ReturnsFailure()
    {
        var fakePdfContent = Encoding.UTF8.GetBytes("THIS IS NOT A REAL PDF FILE");
        var file = CreateMockFormFile("fake.pdf", "application/pdf", fakePdfContent);

        var result = _validator.Validate(file);

        Assert.False(result.IsValid);
        Assert.Equal("Invalid file content: header does not match PDF format.", result.ErrorMessage);
    }

    [Fact]
    public void Validate_ValidPdfUnder5MB_ReturnsSuccess()
    {
        var pdfContent = CreatePdfContent(1024 * 1024); // 1 MB valid PDF
        var file = CreateMockFormFile("valid_passport.pdf", "application/pdf", pdfContent);

        var result = _validator.Validate(file);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
    }
}
