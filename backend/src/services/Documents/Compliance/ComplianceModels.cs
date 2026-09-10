namespace Custodian.Documents.Compliance;

public static class ComplianceStatus
{
    public const string Compliant = "Compliant";
    public const string Rejected = "Rejected";
    public const string Pending = "Pending";
}

public sealed record DocumentValidationContext(
    string DocumentType,
    DateTime? IssueDate,
    DateTime? ExpiryDate,
    DateTime UploadedAtUtc,
    string? FileName = null);

public sealed class ComplianceRuleDefinition
{
    public string DocumentType { get; set; } = string.Empty;
    public int? MaxAgeDays { get; set; }
    public bool RequiresIssueDate { get; set; }
    public bool RequiresExpiryDate { get; set; }
    public string? Description { get; set; }
}

public sealed record ComplianceRuleResult(
    string RuleName,
    bool Passed,
    string? RejectionReason = null)
{
    public static ComplianceRuleResult Success(string ruleName) =>
        new(ruleName, true, null);

    public static ComplianceRuleResult Failure(string ruleName, string rejectionReason) =>
        new(ruleName, false, rejectionReason);
}

public sealed class ComplianceEvaluationResult
{
    public string Status { get; set; } = ComplianceStatus.Pending;
    public bool IsCompliant => Status == ComplianceStatus.Compliant;
    public string? RejectionReason { get; set; }
    public IReadOnlyList<ComplianceRuleResult> EvaluatedRules { get; set; } = Array.Empty<ComplianceRuleResult>();
    public DateTime ValidatedAtUtc { get; set; } = DateTime.UtcNow;

    public static ComplianceEvaluationResult Compliant(IReadOnlyList<ComplianceRuleResult> evaluatedRules) =>
        new()
        {
            Status = ComplianceStatus.Compliant,
            RejectionReason = null,
            EvaluatedRules = evaluatedRules,
            ValidatedAtUtc = DateTime.UtcNow
        };

    public static ComplianceEvaluationResult Rejected(string rejectionReason, IReadOnlyList<ComplianceRuleResult> evaluatedRules) =>
        new()
        {
            Status = ComplianceStatus.Rejected,
            RejectionReason = rejectionReason,
            EvaluatedRules = evaluatedRules,
            ValidatedAtUtc = DateTime.UtcNow
        };
}
