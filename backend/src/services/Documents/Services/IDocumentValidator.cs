using Microsoft.AspNetCore.Http;

namespace Custodian.Documents.Services;

public record DocumentValidationResult(bool IsValid, string? ErrorMessage = null)
{
    public static DocumentValidationResult Success() => new(true);
    public static DocumentValidationResult Failure(string errorMessage) => new(false, errorMessage);
}

public interface IDocumentValidator
{
    DocumentValidationResult Validate(IFormFile? file);
}
