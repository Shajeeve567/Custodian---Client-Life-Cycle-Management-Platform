namespace Custodian.Shared.Messaging;

public static class EventTypes
{
    public const string UserCreated = "user.created";
    public const string TenantCreated = "tenant.created";
    public const string DocumentValidated = "document.validated";
    public const string DocumentCompliant = "document.compliant";
    public const string DocumentRejected = "document.rejected";
    public const string DocumentVerified = "document.verified";
    public const string DocumentVerificationRejected = "document.verification_rejected";
}