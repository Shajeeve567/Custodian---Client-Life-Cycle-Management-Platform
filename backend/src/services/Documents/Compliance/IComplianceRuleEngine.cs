namespace Custodian.Documents.Compliance;

public interface IComplianceRuleEngine
{
    ComplianceEvaluationResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDefinition);

    ComplianceEvaluationResult Evaluate(
        string documentType,
        DateTime? issueDate,
        DateTime? expiryDate,
        ComplianceRuleDefinition? ruleDefinition,
        string? fileName = null);
}
