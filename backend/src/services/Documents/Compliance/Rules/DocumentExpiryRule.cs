namespace Custodian.Documents.Compliance.Rules;

public sealed class DocumentExpiryRule : IComplianceRule
{
    public const string Name = "DocumentExpiry";
    public string RuleName => Name;

    public ComplianceRuleResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDef)
    {
        var nowUtc = context.UploadedAtUtc != default ? context.UploadedAtUtc : DateTime.UtcNow;
        var requiresExpiryDate = ruleDef?.RequiresExpiryDate ?? false;

        // If document type requires expiry date and none provided
        if (requiresExpiryDate && !context.ExpiryDate.HasValue)
        {
            return ComplianceRuleResult.Failure(
                RuleName,
                $"Expiry date is mandatory for document type '{context.DocumentType}'.");
        }

        // If expiry date is present, check whether it has already passed
        if (context.ExpiryDate.HasValue && context.ExpiryDate.Value.Date < nowUtc.Date)
        {
            return ComplianceRuleResult.Failure(
                RuleName,
                $"Document expired on {context.ExpiryDate.Value:yyyy-MM-dd}. Please provide an unexpired document.");
        }

        return ComplianceRuleResult.Success(RuleName);
    }
}
