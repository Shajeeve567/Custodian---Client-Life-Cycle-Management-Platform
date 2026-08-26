using Custodian.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Data;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<ClientProfile> Clients => Set<ClientProfile>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();

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
        });
    }
}