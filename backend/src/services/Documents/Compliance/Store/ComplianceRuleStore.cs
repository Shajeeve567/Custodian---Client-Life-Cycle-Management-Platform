using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Custodian.Documents.Compliance.Store;

public sealed class ComplianceRuleStore : IComplianceRuleStore
{
    private readonly ConcurrentDictionary<string, ComplianceRuleDefinition> _rulesByNormalizedType;

    public ComplianceRuleStore(IOptions<ComplianceRuleOptions>? options = null)
    {
        _rulesByNormalizedType = new ConcurrentDictionary<string, ComplianceRuleDefinition>(StringComparer.OrdinalIgnoreCase);

        // 1. Seed standard out-of-the-box defaults for client lifecycle & onboarding
        SeedDefaultRules();

        // 2. Overlay any configuration overrides from options (e.g. appsettings.json)
        if (options?.Value?.Rules != null)
        {
            foreach (var customRule in options.Value.Rules)
            {
                if (!string.IsNullOrWhiteSpace(customRule.DocumentType))
                {
                    var normalizedKey = NormalizeDocumentType(customRule.DocumentType);
                    _rulesByNormalizedType[normalizedKey] = customRule;
                }
            }
        }
    }

    public ComplianceRuleDefinition? GetRule(string? documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
        {
            return null;
        }

        var normalizedKey = NormalizeDocumentType(documentType);
        if (_rulesByNormalizedType.TryGetValue(normalizedKey, out var rule))
        {
            return rule;
        }

        return null;
    }

    public IReadOnlyList<ComplianceRuleDefinition> GetAllRules()
    {
        return _rulesByNormalizedType.Values.ToList();
    }

    private void SeedDefaultRules()
    {
        var defaults = new List<ComplianceRuleDefinition>
        {
            new()
            {
                DocumentType = "ProofOfAddress",
                MaxAgeDays = 90,
                RequiresIssueDate = true,
                RequiresExpiryDate = false,
                Description = "Proof of residential or corporate address (issued within 90 days)."
            },
            new()
            {
                DocumentType = "UtilityBill",
                MaxAgeDays = 90,
                RequiresIssueDate = true,
                RequiresExpiryDate = false,
                Description = "Recent utility bill for address verification (issued within 90 days)."
            },
            new()
            {
                DocumentType = "BankStatement",
                MaxAgeDays = 90,
                RequiresIssueDate = true,
                RequiresExpiryDate = false,
                Description = "Bank statement for financial standing verification (issued within 90 days)."
            },
            new()
            {
                DocumentType = "Passport",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = true,
                Description = "Official government passport (must not be expired)."
            },
            new()
            {
                DocumentType = "NationalId",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = true,
                Description = "Government issued national identification card (must not be expired)."
            },
            new()
            {
                DocumentType = "GovernmentId",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = true,
                Description = "Official government identification document (must not be expired)."
            },
            new()
            {
                DocumentType = "DriversLicense",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = true,
                Description = "Valid driver's license (must not be expired)."
            },
            new()
            {
                DocumentType = "CertificateOfIncorporation",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = false,
                Description = "Company certificate of incorporation / registration."
            },
            new()
            {
                DocumentType = "CompanyRegistration",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = false,
                Description = "Company registry filing proof."
            },
            new()
            {
                DocumentType = "TaxDeclaration",
                MaxAgeDays = 365,
                RequiresIssueDate = true,
                RequiresExpiryDate = false,
                Description = "Annual tax declaration or return (valid for 1 year)."
            },
            new()
            {
                DocumentType = "FinancialStatement",
                MaxAgeDays = 365,
                RequiresIssueDate = true,
                RequiresExpiryDate = false,
                Description = "Audited annual financial statement (valid for 1 year)."
            },
            new()
            {
                DocumentType = "ComplianceEvidence",
                MaxAgeDays = null,
                RequiresIssueDate = false,
                RequiresExpiryDate = false,
                Description = "General compliance evidence document."
            }
        };

        foreach (var def in defaults)
        {
            var key = NormalizeDocumentType(def.DocumentType);
            _rulesByNormalizedType[key] = def;
        }
    }

    public static string NormalizeDocumentType(string documentType)
    {
        if (string.IsNullOrWhiteSpace(documentType))
        {
            return string.Empty;
        }

        // Strips spaces, underscores, and hyphens and converts to lower-case for robust matching
        return Regex.Replace(documentType.Trim(), @"[\s_\-]+", string.Empty).ToLowerInvariant();
    }
}
