namespace Custodian.Documents.Compliance.Rules;

public interface IComplianceRule
{
    string RuleName { get; }
    ComplianceRuleResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDef);
}
