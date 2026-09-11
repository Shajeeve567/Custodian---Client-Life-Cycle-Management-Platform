namespace Custodian.Documents.Compliance.Store;

public interface IComplianceRuleStore
{
    /// <summary>
    /// Retrieves the compliance rule definition for a given document type.
    /// Returns null or default definition if no specific rule is configured.
    /// </summary>
    ComplianceRuleDefinition? GetRule(string? documentType);

    /// <summary>
    /// Returns all registered compliance rule definitions.
    /// </summary>
    IReadOnlyList<ComplianceRuleDefinition> GetAllRules();
}
