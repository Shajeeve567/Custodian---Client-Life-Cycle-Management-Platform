namespace Custodian.Identity.Services.Notifications.Strategies;

public sealed class ResendOptions
{
    public const string SectionName = "Resend";

    public string? ApiKey { get; set; }
    public string FromEmail { get; set; } = "Custodian <notifications@custodian.platform>";
    public string ApiUrl { get; set; } = "https://api.resend.com";
}
