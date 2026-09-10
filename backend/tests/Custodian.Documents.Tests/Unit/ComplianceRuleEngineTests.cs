using Custodian.Documents.Compliance;
using Custodian.Documents.Compliance.Rules;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class ComplianceRuleEngineTests
{
    private readonly ComplianceRuleEngine _engine;

    public ComplianceRuleEngineTests()
    {
        var rules = new IComplianceRule[]
        {
            new DocumentFreshnessRule(),
            new DocumentExpiryRule()
        };
        _engine = new ComplianceRuleEngine(rules);
    }

    [Fact]
    public void Evaluate_FreshDocument_WithinMaxAge_ReturnsCompliant()
    {
        // Arrange: Utility Bill issued 30 days ago, max-age 90 days
        var now = DateTime.UtcNow;
        var context = new DocumentValidationContext(
            DocumentType: "UtilityBill",
            IssueDate: now.AddDays(-30),
            ExpiryDate: null,
            UploadedAtUtc: now,
            FileName: "bill.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "UtilityBill",
            MaxAgeDays = 90,
            RequiresIssueDate = true,
            RequiresExpiryDate = false
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.True(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Compliant, result.Status);
        Assert.Null(result.RejectionReason);
        Assert.Equal(2, result.EvaluatedRules.Count);
        Assert.All(result.EvaluatedRules, r => Assert.True(r.Passed));
    }

    [Fact]
    public void Evaluate_OutdatedDocument_ExceedingMaxAge_ReturnsRejected_WithHumanReadableReason()
    {
        // Arrange: Proof of Address issued 120 days ago, max-age 90 days
        var now = DateTime.UtcNow;
        var issueDate = now.AddDays(-120);
        var context = new DocumentValidationContext(
            DocumentType: "ProofOfAddress",
            IssueDate: issueDate,
            ExpiryDate: null,
            UploadedAtUtc: now,
            FileName: "address_proof.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "ProofOfAddress",
            MaxAgeDays = 90,
            RequiresIssueDate = true,
            RequiresExpiryDate = false
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.False(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("exceeds maximum allowable age of 90 days", result.RejectionReason);
        Assert.Contains(issueDate.ToString("yyyy-MM-dd"), result.RejectionReason);
    }

    [Fact]
    public void Evaluate_FutureIssueDate_ReturnsRejected()
    {
        // Arrange: Issue date tomorrow
        var now = DateTime.UtcNow;
        var futureDate = now.AddDays(2);
        var context = new DocumentValidationContext(
            DocumentType: "BankStatement",
            IssueDate: futureDate,
            ExpiryDate: null,
            UploadedAtUtc: now,
            FileName: "statement.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "BankStatement",
            MaxAgeDays = 90,
            RequiresIssueDate = true,
            RequiresExpiryDate = false
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.False(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("cannot be in the future", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_MissingIssueDate_WhenFreshnessRequired_ReturnsRejected()
    {
        // Arrange: MaxAge defined but no IssueDate provided
        var now = DateTime.UtcNow;
        var context = new DocumentValidationContext(
            DocumentType: "BankStatement",
            IssueDate: null,
            ExpiryDate: null,
            UploadedAtUtc: now,
            FileName: "statement.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "BankStatement",
            MaxAgeDays = 90,
            RequiresIssueDate = true,
            RequiresExpiryDate = false
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.False(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("Issue date is required to verify document freshness", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_ValidFutureExpiry_ReturnsCompliant()
    {
        // Arrange: Passport expiring in 2 years
        var now = DateTime.UtcNow;
        var context = new DocumentValidationContext(
            DocumentType: "Passport",
            IssueDate: now.AddYears(-1),
            ExpiryDate: now.AddYears(2),
            UploadedAtUtc: now,
            FileName: "passport.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "Passport",
            MaxAgeDays = null,
            RequiresIssueDate = false,
            RequiresExpiryDate = true
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.True(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Compliant, result.Status);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Evaluate_ExpiredDocument_ReturnsRejected_WithHumanReadableReason()
    {
        // Arrange: Passport expired 10 days ago
        var now = DateTime.UtcNow;
        var expiredDate = now.AddDays(-10);
        var context = new DocumentValidationContext(
            DocumentType: "Passport",
            IssueDate: now.AddYears(-10),
            ExpiryDate: expiredDate,
            UploadedAtUtc: now,
            FileName: "old_passport.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "Passport",
            MaxAgeDays = null,
            RequiresIssueDate = false,
            RequiresExpiryDate = true
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.False(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("Document expired on", result.RejectionReason);
        Assert.Contains(expiredDate.ToString("yyyy-MM-dd"), result.RejectionReason);
    }

    [Fact]
    public void Evaluate_MissingExpiryDate_WhenRequiresExpiryDate_ReturnsRejected()
    {
        // Arrange: Passport requires expiry date, none provided
        var now = DateTime.UtcNow;
        var context = new DocumentValidationContext(
            DocumentType: "Passport",
            IssueDate: now.AddYears(-1),
            ExpiryDate: null,
            UploadedAtUtc: now,
            FileName: "passport.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "Passport",
            MaxAgeDays = null,
            RequiresIssueDate = false,
            RequiresExpiryDate = true
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.False(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("Expiry date is mandatory for document type 'Passport'", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_DocumentWithNoRulesConfigured_ReturnsCompliant()
    {
        // Arrange: General evidence with no expiry or freshness required
        var now = DateTime.UtcNow;
        var context = new DocumentValidationContext(
            DocumentType: "GeneralContract",
            IssueDate: now.AddYears(-2),
            ExpiryDate: null,
            UploadedAtUtc: now,
            FileName: "contract.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "GeneralContract",
            MaxAgeDays = null,
            RequiresIssueDate = false,
            RequiresExpiryDate = false
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.True(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Compliant, result.Status);
        Assert.Null(result.RejectionReason);
    }

    [Fact]
    public void Evaluate_MultipleRuleViolations_AggregatesRejectionReasonsCleanly()
    {
        // Arrange: Document with both outdated issue date AND expired expiry date
        var now = DateTime.UtcNow;
        var context = new DocumentValidationContext(
            DocumentType: "CustomCertification",
            IssueDate: now.AddDays(-200),
            ExpiryDate: now.AddDays(-10),
            UploadedAtUtc: now,
            FileName: "cert.pdf");

        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "CustomCertification",
            MaxAgeDays = 90,
            RequiresIssueDate = true,
            RequiresExpiryDate = true
        };

        // Act
        var result = _engine.Evaluate(context, ruleDef);

        // Assert
        Assert.False(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, result.Status);
        Assert.NotNull(result.RejectionReason);
        Assert.Contains("exceeds maximum allowable age of 90 days", result.RejectionReason);
        Assert.Contains("Document expired on", result.RejectionReason);
    }

    [Fact]
    public void Evaluate_ConvenienceOverload_WorksEquivalently()
    {
        var now = DateTime.UtcNow;
        var ruleDef = new ComplianceRuleDefinition
        {
            DocumentType = "UtilityBill",
            MaxAgeDays = 90,
            RequiresIssueDate = true
        };

        var result = _engine.Evaluate(
            documentType: "UtilityBill",
            issueDate: now.AddDays(-20),
            expiryDate: null,
            ruleDefinition: ruleDef,
            fileName: "sample.pdf");

        Assert.True(result.IsCompliant);
        Assert.Equal(ComplianceStatus.Compliant, result.Status);
    }
}
