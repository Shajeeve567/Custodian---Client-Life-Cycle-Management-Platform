namespace Custodian.Documents.Compliance.Rules;

public sealed class DocumentFreshnessRule : IComplianceRule
{
    public const string Name = "DocumentFreshness";
    public string RuleName => Name;

    public ComplianceRuleResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDef)
    {
        var nowUtc = context.UploadedAtUtc != default ? context.UploadedAtUtc : DateTime.UtcNow;

        // Future issue date check: regardless of max-age, an issue date cannot be in the future
        if (context.IssueDate.HasValue && context.IssueDate.Value.Date > nowUtc.Date)
        {
            return ComplianceRuleResult.Failure(
                RuleName,
                $"Document issue date ({context.IssueDate.Value:yyyy-MM-dd}) cannot be in the future.");
        }

        // Freshness check applies if rule definition specifies MaxAgeDays or RequiresIssueDate
        var maxAgeDays = ruleDef?.MaxAgeDays;
        var requiresIssueDate = ruleDef?.RequiresIssueDate ?? false;

        if (maxAgeDays.HasValue || requiresIssueDate)
        {
            if (!context.IssueDate.HasValue)
            {
                return ComplianceRuleResult.Failure(
                    RuleName,
                    $"Issue date is required to verify document freshness for type '{context.DocumentType}'.");
            }

            if (maxAgeDays.HasValue)
            {
                var ageInDays = (int)(nowUtc.Date - context.IssueDate.Value.Date).TotalDays;
                if (ageInDays > maxAgeDays.Value)
                {
                    return ComplianceRuleResult.Failure(
                        RuleName,
                        $"Document exceeds maximum allowable age of {maxAgeDays.Value} days (issued {ageInDays} days ago on {context.IssueDate.Value:yyyy-MM-dd}). Please upload a recent copy.");
                }
            }
        }

        return ComplianceRuleResult.Success(RuleName);
    }
}
