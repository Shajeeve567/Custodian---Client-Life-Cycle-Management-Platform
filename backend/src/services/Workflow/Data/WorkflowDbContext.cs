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
    public DbSet<Requirement> Requirements => Set<Requirement>();
    public DbSet<EngagementCondition> EngagementConditions => Set<EngagementCondition>();

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

            entity.Property(e => e.Stage)
                .HasColumnName("stage")
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(EngagementStage.Onboarding)
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
            entity.Property(a => a.StageNumber).HasColumnName("stage_number").HasDefaultValue(1).IsRequired();
            entity.Property(a => a.DeadlineUtc).HasColumnName("deadline_utc");
            entity.Property(a => a.ActivatedAt).HasColumnName("activated_at");
            entity.Property(a => a.Source).HasColumnName("source").HasMaxLength(100).IsRequired();
            entity.Property(a => a.SourceType).HasColumnName("source_type").HasMaxLength(30).IsRequired();
            entity.Property(a => a.IsInternalOnly).HasColumnName("is_internal_only").IsRequired();
            entity.Property(a => a.AssignedToRole).HasColumnName("assigned_to_role").HasMaxLength(50);
            entity.Property(a => a.CompletedByActor).HasColumnName("completed_by_actor").HasMaxLength(100);
            entity.Property(a => a.CompletedAt).HasColumnName("completed_at");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(a => a.UpdatedAt).HasColumnName("updated_at").IsRequired();
            entity.Property(a => a.SourceMetadata).HasColumnName("source_metadata");
            entity.Property(a => a.LinkedRequirementId).HasColumnName("linked_requirement_id");
            entity.Property(a => a.LinkedDocumentId).HasColumnName("linked_document_id");
            entity.Property(a => a.LinkedConditionId).HasColumnName("linked_condition_id");
            entity.Property(a => a.LinkedMeetingId).HasColumnName("linked_meeting_id");

            entity.HasIndex(a => a.TenantId).HasDatabaseName("idx_action_tenant_id");
            entity.HasIndex(a => a.EngagementId).HasDatabaseName("idx_action_engagement_id");
            entity.HasIndex(a => new { a.TenantId, a.EngagementId }).HasDatabaseName("idx_action_tenant_engagement");
            entity.HasIndex(a => new { a.EngagementId, a.StageNumber }).HasDatabaseName("idx_action_engagement_stage");
            entity.HasIndex(a => a.LinkedRequirementId).HasDatabaseName("idx_action_linked_requirement");
            // CSTD-21 (21-N1): SLA/stall and next-action queries; linked document lookups (CSTD-19 consumer)
            entity.HasIndex(a => new { a.TenantId, a.EngagementId, a.Status }).HasDatabaseName("idx_action_tenant_engagement_status");
            entity.HasIndex(a => new { a.TenantId, a.Status, a.DeadlineUtc }).HasDatabaseName("idx_action_tenant_status_deadline");
            entity.HasIndex(a => a.LinkedDocumentId).HasDatabaseName("idx_action_linked_document");
        });

        modelBuilder.Entity<Requirement>(entity =>
        {
            entity.ToTable("requirements");

            entity.HasKey(r => r.RequirementId);

            entity.Property(r => r.RequirementId).HasColumnName("requirement_id");
            entity.Property(r => r.EngagementId).HasColumnName("engagement_id").IsRequired();
            entity.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(36).IsRequired();
            entity.Property(r => r.Type).HasColumnName("type").HasMaxLength(100).IsRequired();
            entity.Property(r => r.Status).HasColumnName("status").HasMaxLength(30).IsRequired();
            entity.Property(r => r.AssignedToRole).HasColumnName("assigned_to_role").HasMaxLength(50);
            entity.Property(r => r.Value).HasColumnName("value");
            entity.Property(r => r.StageNumber).HasColumnName("stage_number");
            entity.Property(r => r.RequestedBy).HasColumnName("requested_by").HasMaxLength(100);
            entity.Property(r => r.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(100);
            entity.Property(r => r.RequestedAt).HasColumnName("requested_at");
            entity.Property(r => r.SubmittedAt).HasColumnName("submitted_at");
            entity.Property(r => r.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(r => r.RejectionReason).HasColumnName("rejection_reason");
            entity.Property(r => r.CreatedAt).HasColumnName("created_at").IsRequired();

            entity.HasIndex(r => r.TenantId).HasDatabaseName("idx_requirement_tenant_id");
            entity.HasIndex(r => r.EngagementId).HasDatabaseName("idx_requirement_engagement_id");
            entity.HasIndex(r => new { r.TenantId, r.EngagementId }).HasDatabaseName("idx_requirement_tenant_engagement");
        });

        modelBuilder.Entity<EngagementCondition>(entity =>
        {
            entity.ToTable("engagement_conditions");

            entity.HasKey(c => c.ConditionId);

            entity.Property(c => c.ConditionId).HasColumnName("condition_id");
            entity.Property(c => c.EngagementId).HasColumnName("engagement_id").IsRequired();
            entity.Property(c => c.TenantId).HasColumnName("tenant_id").HasMaxLength(36).IsRequired();
            entity.Property(c => c.Type).HasColumnName("type").HasMaxLength(20).IsRequired();
            entity.Property(c => c.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
            entity.Property(c => c.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue(ConditionStatus.Pending).IsRequired();
            entity.Property(c => c.RequiredBeforeStage)
                .HasColumnName("required_before_stage")
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(EngagementStage.Execution)
                // Always send the application's value: Onboarding is the enum's CLR default, so without
                // this EF would silently substitute the database default (Execution) for it.
                .ValueGeneratedNever()
                .IsRequired();
            entity.Property(c => c.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            entity.Property(c => c.Description).HasColumnName("description");
            entity.Property(c => c.DueDateUtc).HasColumnName("due_date_utc");
            entity.Property(c => c.Amount).HasColumnName("amount").HasPrecision(18, 2);
            entity.Property(c => c.Currency).HasColumnName("currency").HasMaxLength(3);
            entity.Property(c => c.PaymentType).HasColumnName("payment_type").HasMaxLength(30);
            entity.Property(c => c.InternalNote).HasColumnName("internal_note");
            entity.Property(c => c.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired();
            entity.Property(c => c.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(c => c.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);
            entity.Property(c => c.UpdatedAt).HasColumnName("updated_at");
            entity.Property(c => c.DeactivatedBy).HasColumnName("deactivated_by").HasMaxLength(100);
            entity.Property(c => c.DeactivatedAt).HasColumnName("deactivated_at");
            entity.Property(c => c.DeactivationReason).HasColumnName("deactivation_reason");
            entity.Property(c => c.SatisfiedAt).HasColumnName("satisfied_at");
            entity.Property(c => c.SatisfiedBy).HasColumnName("satisfied_by").HasMaxLength(100);

            entity.HasIndex(c => c.TenantId).HasDatabaseName("idx_condition_tenant_id");
            entity.HasIndex(c => c.EngagementId).HasDatabaseName("idx_condition_engagement_id");
            entity.HasIndex(c => new { c.TenantId, c.EngagementId }).HasDatabaseName("idx_condition_tenant_engagement");
            entity.HasIndex(c => new { c.TenantId, c.EngagementId, c.Type, c.IsActive }).HasDatabaseName("idx_condition_tenant_eng_type_active");
        });
    }
}
