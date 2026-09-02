using Custodian.Shared.Tenancy;
using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class IdentityDbContext : DbContext
{
    private readonly TenantContext? _tenantContext;

    public IdentityDbContext(DbContextOptions<IdentityDbContext> options, TenantContext? tenantContext = null) 
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    private Guid CurrentTenantId => Guid.TryParse(_tenantContext?.TenantId, out var id) ? id : Guid.Empty;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<ClientProfile> Clients => Set<ClientProfile>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).ValueGeneratedNever();
            entity.Property(t => t.Name).HasMaxLength(200).IsRequired();
            entity.Property(t => t.CreatedAtUtc).IsRequired();
            // Client relationship remains the same
            entity.HasMany(t => t.Clients)
                .WithOne(c => c.Tenant)
                .HasForeignKey(c => c.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).ValueGeneratedNever();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.Email).HasMaxLength(320).IsRequired();
            entity.Property(u => u.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
            
            // Global Query Filter for Many-to-Many
            entity.HasQueryFilter(u => u.Memberships.Any(m => m.TenantId == CurrentTenantId));
        });

        modelBuilder.Entity<TenantMembership>(entity =>
        {
            // Composite primary key
            entity.HasKey(tm => new { tm.UserId, tm.TenantId });
            entity.Property(tm => tm.Role).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(tm => tm.User)
                .WithMany(u => u.Memberships)
                .HasForeignKey(tm => tm.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(tm => tm.Tenant)
                .WithMany(t => t.Memberships)
                .HasForeignKey(tm => tm.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientProfile>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).ValueGeneratedNever();
            entity.HasIndex(c => new { c.TenantId, c.Email });
            entity.Property(c => c.Email).HasMaxLength(320).IsRequired();
            entity.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            
            // Global Query Filter
            entity.HasQueryFilter(c => c.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(n => n.NotificationId);
            entity.Property(n => n.NotificationId).ValueGeneratedNever();
            entity.Property(n => n.TenantId).IsRequired();
            entity.Property(n => n.ClientId).IsRequired();
            entity.Property(n => n.Message).HasMaxLength(2000).IsRequired();
            entity.Property(n => n.SourceEventType).HasMaxLength(100).IsRequired();
            entity.Property(n => n.IsRead).IsRequired().HasDefaultValue(false);
            entity.Property(n => n.CreatedAt).IsRequired();

            entity.HasOne(n => n.Client)
                .WithMany()
                .HasForeignKey(n => n.ClientId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(n => new { n.TenantId, n.ClientId, n.IsRead });

            // Global Query Filter
            entity.HasQueryFilter(n => n.TenantId == CurrentTenantId);
        });
    }
}