using Custodian.Workflow.Domain;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Data;

public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : base(options)
    {
    }

    public DbSet<Engagement> Engagements => Set<Engagement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Engagement>(entity =>
        {
            entity.ToTable("engagements");

            entity.HasKey(e => e.EngagementId);

            entity.Property(e => e.EngagementId)
                .HasColumnName("engagement_id")
                .HasMaxLength(36);

            entity.Property(e => e.TenantId)
                .HasColumnName("tenant_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(e => e.ClientId)
                .HasColumnName("client_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(e => e.StaffId)
                .HasColumnName("staff_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(e => e.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.Property(e => e.ClosedAt)
                .HasColumnName("closed_at");

            entity.HasIndex(e => e.TenantId).HasDatabaseName("idx_tenant_id");
            entity.HasIndex(e => e.ClientId).HasDatabaseName("idx_client_id");
            entity.HasIndex(e => e.StaffId).HasDatabaseName("idx_staff_id");
            entity.HasIndex(e => new { e.TenantId, e.Status }).HasDatabaseName("idx_tenant_status");
        });
    }
}
