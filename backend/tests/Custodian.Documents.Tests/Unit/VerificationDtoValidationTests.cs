using System.ComponentModel.DataAnnotations;
using Custodian.Documents.DTOs;
using Custodian.Shared.Contracts;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class VerificationDtoValidationTests
{
    [Fact]
    public void RejectDocumentRequestDto_EmptyReason_FailsValidation()
    {
        var dto = new RejectDocumentRequestDto
        {
            Reason = string.Empty,
            StaffActor = "staff-1"
        };

        var context = new ValidationContext(dto);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(dto, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("Reason"));
    }

    [Fact]
    public void RejectDocumentRequestDto_ValidReason_PassesValidation()
    {
        var dto = new RejectDocumentRequestDto
        {
            Reason = "The photo is too blurry to read the document number.",
            StaffActor = "staff-1"
        };

        var context = new ValidationContext(dto);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(dto, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void VerifyDocumentRequestDto_OptionalStaffNotes_PassesValidationWhenEmpty()
    {
        var dto = new VerifyDocumentRequestDto
        {
            StaffNotes = null,
            StaffActor = "staff-1"
        };

        var context = new ValidationContext(dto);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(dto, context, results, validateAllProperties: true);

        Assert.True(isValid);
        Assert.Empty(results);
    }

    [Fact]
    public void VerifyDocumentRequestDto_ExceedingLengthNotes_FailsValidation()
    {
        var dto = new VerifyDocumentRequestDto
        {
            StaffNotes = new string('A', 501),
            StaffActor = "staff-1"
        };

        var context = new ValidationContext(dto);
        var results = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(dto, context, results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, r => r.MemberNames.Contains("StaffNotes"));
    }

    [Fact]
    public void DocumentResponseDto_InitializesWithUnverifiedStatusByDefault()
    {
        var dto = new DocumentResponseDto();

        Assert.Equal(DocumentVerificationStatus.Unverified, dto.VerificationStatus);
        Assert.Null(dto.VerifiedBy);
        Assert.Null(dto.VerifiedAt);
        Assert.Null(dto.VerificationReason);
    }
}
