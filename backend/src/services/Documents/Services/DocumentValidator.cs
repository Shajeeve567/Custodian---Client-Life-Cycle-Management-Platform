using Microsoft.AspNetCore.Http;

namespace Custodian.Documents.Services;

public class DocumentValidator : IDocumentValidator
{
    public const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB
    public const string AllowedExtension = ".pdf";
    public const string AllowedContentType = "application/pdf";
    private static readonly byte[] PdfMagicBytes = "%PDF-"u8.ToArray();

    public DocumentValidationResult Validate(IFormFile? file)
    {
        if (file == null || file.Length == 0)
        {
            return DocumentValidationResult.Failure("File is required and cannot be empty.");
        }

        if (file.Length > MaxFileSizeBytes)
        {
            return DocumentValidationResult.Failure("File size exceeds maximum limit of 5 MB.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(extension) || !extension.Equals(AllowedExtension, StringComparison.OrdinalIgnoreCase))
        {
            return DocumentValidationResult.Failure("Only PDF files are allowed.");
        }

        if (string.IsNullOrEmpty(file.ContentType) || !file.ContentType.Equals(AllowedContentType, StringComparison.OrdinalIgnoreCase))
        {
            return DocumentValidationResult.Failure("Only PDF files are allowed.");
        }

        try
        {
            using var stream = file.OpenReadStream();
            if (stream.CanRead)
            {
                var buffer = new byte[PdfMagicBytes.Length];
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead < PdfMagicBytes.Length || !buffer.SequenceEqual(PdfMagicBytes))
                {
                    return DocumentValidationResult.Failure("Invalid file content: header does not match PDF format.");
                }
            }
        }
        catch (Exception ex)
        {
            return DocumentValidationResult.Failure($"Unable to read file stream: {ex.Message}");
        }

        return DocumentValidationResult.Success();
    }
}
