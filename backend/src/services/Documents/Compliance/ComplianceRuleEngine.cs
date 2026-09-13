using Custodian.Documents.Compliance.Rules;
using Custodian.Documents.Compliance.Store;

namespace Custodian.Documents.Compliance;

public sealed class ComplianceRuleEngine : IComplianceRuleEngine
{
    private readonly IReadOnlyList<IComplianceRule> _rules;
    private readonly IComplianceRuleStore? _ruleStore;

    public ComplianceRuleEngine(
        IEnumerable<IComplianceRule> rules,
        IComplianceRuleStore? ruleStore = null)
    {
        _rules = rules?.ToList() ?? new List<IComplianceRule>();
        _ruleStore = ruleStore;
    }

    public ComplianceEvaluationResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDefinition = null)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        // If rule definition is not explicitly supplied, look it up in the rule store
        var effectiveRuleDef = ruleDefinition ?? _ruleStore?.GetRule(context.DocumentType);

        var results = new List<ComplianceRuleResult>(_rules.Count);

        foreach (var rule in _rules)
        {
            var ruleResult = rule.Evaluate(context, effectiveRuleDef);
            results.Add(ruleResult);
        }

        var failures = results.Where(r => !r.Passed).ToList();
        if (failures.Count > 0)
        {
            var aggregatedReason = string.Join("; ", failures.Select(f => f.RejectionReason).Where(r => !string.IsNullOrWhiteSpace(r)));
            return ComplianceEvaluationResult.Rejected(aggregatedReason, results);
        }

        return ComplianceEvaluationResult.Compliant(results);
    }

    public ComplianceEvaluationResult Evaluate(
        string documentType,
        DateTime? issueDate,
        DateTime? expiryDate,
        ComplianceRuleDefinition? ruleDefinition = null,
        string? fileName = null)
    {
        var context = new DocumentValidationContext(
            DocumentType: documentType ?? string.Empty,
            IssueDate: issueDate,
            ExpiryDate: expiryDate,
            UploadedAtUtc: DateTime.UtcNow,
            FileName: fileName);

        return Evaluate(context, ruleDefinition);
    }
}
