using Custodian.Documents.Compliance;
using Custodian.Documents.Compliance.Rules;
using Custodian.Documents.Compliance.Store;
using Microsoft.Extensions.Options;
using Xunit;

namespace Custodian.Documents.Tests.Unit;

public class ComplianceRuleStoreTests
{
    private readonly ComplianceRuleStore _store;

    public ComplianceRuleStoreTests()
    {
        _store = new ComplianceRuleStore();
    }

    [Theory]
    [InlineData("ProofOfAddress", 90, true, false)]
    [InlineData("UtilityBill", 90, true, false)]
    [InlineData("BankStatement", 90, true, false)]
    [InlineData("Passport", null, false, true)]
    [InlineData("NationalId", null, false, true)]
    [InlineData("GovernmentId", null, false, true)]
    [InlineData("DriversLicense", null, false, true)]
    [InlineData("CertificateOfIncorporation", null, false, false)]
    [InlineData("CompanyRegistration", null, false, false)]
    [InlineData("TaxDeclaration", 365, true, false)]
    [InlineData("FinancialStatement", 365, true, false)]
    [InlineData("ComplianceEvidence", null, false, false)]
    public void GetRule_ReturnsPreseededDefaults_ForStandardDocumentTypes(
        string docType,
        int? expectedMaxAge,
        bool expectedRequiresIssueDate,
        bool expectedRequiresExpiryDate)
    {
        var rule = _store.GetRule(docType);

        Assert.NotNull(rule);
        Assert.Equal(expectedMaxAge, rule.MaxAgeDays);
        Assert.Equal(expectedRequiresIssueDate, rule.RequiresIssueDate);
        Assert.Equal(expectedRequiresExpiryDate, rule.RequiresExpiryDate);
    }

    [Theory]
    [InlineData("proof_of_address")]
    [InlineData("PROOF-OF-ADDRESS")]
    [InlineData("Proof Of Address")]
    [InlineData("  proofofaddress  ")]
    [InlineData("UTILITY_BILL")]
    [InlineData("utility-bill")]
    [InlineData("PASSPORT")]
    public void GetRule_CaseAndFormatInsensitive_ResolvesMatchingRule(string formattedType)
    {
        var rule = _store.GetRule(formattedType);

        Assert.NotNull(rule);
    }

    [Fact]
    public void GetRule_UnknownDocumentType_ReturnsNull()
    {
        var rule = _store.GetRule("UnknownCustomDocType");

        Assert.Null(rule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetRule_NullOrWhitespace_ReturnsNull(string? emptyType)
    {
        var rule = _store.GetRule(emptyType);

        Assert.Null(rule);
    }

    [Fact]
    public void GetAllRules_ReturnsAllPreseededRules()
    {
        var rules = _store.GetAllRules();

        Assert.NotNull(rules);
        Assert.True(rules.Count >= 12);
        Assert.Contains(rules, r => r.DocumentType == "ProofOfAddress");
        Assert.Contains(rules, r => r.DocumentType == "Passport");
    }

    [Fact]
    public void Constructor_WithOptions_OverridesDefaultsAndAddsNewTypes()
    {
        // Arrange
        var customOptions = new ComplianceRuleOptions
        {
            Rules = new List<ComplianceRuleDefinition>
            {
                // Override ProofOfAddress to 60 days
                new()
                {
                    DocumentType = "ProofOfAddress",
                    MaxAgeDays = 60,
                    RequiresIssueDate = true,
                    RequiresExpiryDate = false
                },
                // Add new custom document type
                new()
                {
                    DocumentType = "VendorInsuranceCertificate",
                    MaxAgeDays = 180,
                    RequiresIssueDate = true,
                    RequiresExpiryDate = true
                }
            }
        };

        var storeWithOptions = new ComplianceRuleStore(Options.Create(customOptions));

        // Act
        var overriddenRule = storeWithOptions.GetRule("ProofOfAddress");
        var customRule = storeWithOptions.GetRule("VendorInsuranceCertificate");

        // Assert
        Assert.NotNull(overriddenRule);
        Assert.Equal(60, overriddenRule.MaxAgeDays);

        Assert.NotNull(customRule);
        Assert.Equal(180, customRule.MaxAgeDays);
        Assert.True(customRule.RequiresExpiryDate);
    }

    [Fact]
    public void ComplianceRuleEngine_WithInjectedStore_AutomaticallyResolvesRules()
    {
        // Arrange: ComplianceRuleEngine with store injected
        var rules = new IComplianceRule[]
        {
            new DocumentFreshnessRule(),
            new DocumentExpiryRule()
        };
        var engineWithStore = new ComplianceRuleEngine(rules, _store);

        var now = DateTime.UtcNow;

        // Act: UtilityBill issued 45 days ago without explicitly passing ruleDefinition
        var compliantResult = engineWithStore.Evaluate("UtilityBill", now.AddDays(-45), null);

        // Act: UtilityBill issued 100 days ago (violates 90-day max-age default)
        var rejectedResult = engineWithStore.Evaluate("UtilityBill", now.AddDays(-100), null);

        // Assert
        Assert.True(compliantResult.IsCompliant);
        Assert.Equal(ComplianceStatus.Compliant, compliantResult.Status);

        Assert.False(rejectedResult.IsCompliant);
        Assert.Equal(ComplianceStatus.Rejected, rejectedResult.Status);
        Assert.Contains("exceeds maximum allowable age of 90 days", rejectedResult.RejectionReason);
    }
}
