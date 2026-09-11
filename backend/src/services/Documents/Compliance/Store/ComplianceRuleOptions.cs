namespace Custodian.Documents.Compliance.Store;

public sealed class ComplianceRuleOptions
{
    public const string SectionName = "ComplianceRules";

    public List<ComplianceRuleDefinition> Rules { get; set; } = new();
}
