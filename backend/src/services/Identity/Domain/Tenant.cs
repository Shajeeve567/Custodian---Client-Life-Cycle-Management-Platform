namespace Custodian.Identity.Domain;

public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public List<UserAccount> Users { get; set; } = [];
    public List<ClientProfile> Clients { get; set; } = [];
}