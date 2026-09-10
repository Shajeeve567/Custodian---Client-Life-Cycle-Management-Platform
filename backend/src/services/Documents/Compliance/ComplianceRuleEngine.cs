using Custodian.Documents.Compliance.Rules;

namespace Custodian.Documents.Compliance;

public sealed class ComplianceRuleEngine : IComplianceRuleEngine
{
    private readonly IReadOnlyList<IComplianceRule> _rules;

    public ComplianceRuleEngine(IEnumerable<IComplianceRule> rules)
    {
        _rules = rules?.ToList() ?? new List<IComplianceRule>();
    }

    public ComplianceEvaluationResult Evaluate(DocumentValidationContext context, ComplianceRuleDefinition? ruleDefinition)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var results = new List<ComplianceRuleResult>(_rules.Count);

        foreach (var rule in _rules)
        {
            var ruleResult = rule.Evaluate(context, ruleDefinition);
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
        ComplianceRuleDefinition? ruleDefinition,
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
