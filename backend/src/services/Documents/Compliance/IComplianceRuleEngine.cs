namespace Custodian.Documents.Compliance;

public interface IComplianceRuleEngine
{
    /// <summary>
    /// Evaluates the document against compliance rules.
    /// If ruleDefinition is null, it resolves the definition from the registered IComplianceRuleStore.
    /// </summary>
    ComplianceEvaluationResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDefinition = null);

    /// <summary>
    /// Evaluates document parameters against compliance rules.
    /// If ruleDefinition is null, it resolves the definition from the registered IComplianceRuleStore.
    /// </summary>
    ComplianceEvaluationResult Evaluate(
        string documentType,
        DateTime? issueDate,
        DateTime? expiryDate,
        ComplianceRuleDefinition? ruleDefinition = null,
        string? fileName = null);
}
