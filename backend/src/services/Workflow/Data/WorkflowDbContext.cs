using Custodian.Workflow.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Workflow.Data;

public class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : base(options)
    {
    }

    public DbSet<Engagement> Engagements => Set<Engagement>();
    public DbSet<ClientAction> ClientActions => Set<ClientAction>();

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

        modelBuilder.Entity<ClientAction>(entity =>
        {
            entity.ToTable("client_actions");

            entity.HasKey(a => a.ActionId);

            entity.Property(a => a.ActionId).HasColumnName("action_id");
            entity.Property(a => a.EngagementId).HasColumnName("engagement_id").IsRequired();
            entity.Property(a => a.TenantId).HasColumnName("tenant_id").HasMaxLength(36).IsRequired();
            entity.Property(a => a.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            entity.Property(a => a.Description).HasColumnName("description");
            entity.Property(a => a.Type).HasColumnName("type").HasMaxLength(50).IsRequired();
            entity.Property(a => a.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(a => a.Source).HasColumnName("source").HasMaxLength(100).IsRequired();
            entity.Property(a => a.IsInternalOnly).HasColumnName("is_internal_only").IsRequired();
            entity.Property(a => a.AssignedToRole).HasColumnName("assigned_to_role").HasMaxLength(50);
            entity.Property(a => a.CompletedByActor).HasColumnName("completed_by_actor").HasMaxLength(100);
            entity.Property(a => a.CompletedAt).HasColumnName("completed_at");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(a => a.SourceMetadata).HasColumnName("source_metadata");

            entity.HasIndex(a => a.TenantId).HasDatabaseName("idx_action_tenant_id");
            entity.HasIndex(a => a.EngagementId).HasDatabaseName("idx_action_engagement_id");
            entity.HasIndex(a => new { a.TenantId, a.EngagementId }).HasDatabaseName("idx_action_tenant_engagement");
        });
    }
}
