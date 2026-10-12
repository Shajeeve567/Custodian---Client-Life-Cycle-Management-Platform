using Custodian.Audit.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Audit.Data;

public class AuditDbContext : DbContext
{
    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options)
    {
    }

    public DbSet<AuditEvent> Events { get; set; } = null!;

    public DbSet<EngagementChainHead> ChainHeads { get; set; } = null!;

    public DbSet<AuditEventMetadata> EventMetadata { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("events");

            entity.HasKey(e => e.EventId);

            entity.Property(e => e.EventId)
                .HasColumnName("event_id")
                .HasMaxLength(36);

            entity.Property(e => e.EngagementId)
                .HasColumnName("engagement_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(e => e.TenantId)
                .HasColumnName("tenant_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(e => e.Actor)
                .HasColumnName("actor")
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(e => e.Type)
                .HasColumnName("type")
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(e => e.Timestamp)
                .HasColumnName("timestamp")
                .IsRequired();

            // longtext, not json: MySQL's json type re-serialises what it stores (key order, number
            // formatting such as 100.50 -> 100.5), so the payload read back could differ from the
            // payload that was hashed and verification would report tampering that never happened.
            entity.Property(e => e.Payload)
                .HasColumnName("payload")
                .HasColumnType("longtext")
                .IsRequired();

            entity.Property(e => e.SequenceNumber)
                .HasColumnName("sequence_number")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.Hash)
                .HasColumnName("hash")
                .HasMaxLength(64);

            entity.Property(e => e.PreviousHash)
                .HasColumnName("previous_hash")
                .HasMaxLength(64);

            // Performance Indexes
            entity.HasIndex(e => e.SequenceNumber, "idx_sequence_number").IsUnique();
            entity.HasIndex(e => e.EngagementId, "idx_engagement_id");
            entity.HasIndex(e => e.TenantId, "idx_tenant_id");
            entity.HasIndex(e => new { e.TenantId, e.EngagementId }, "idx_tenant_engagement");
            entity.HasIndex(e => e.Type, "idx_type");

            // Chain verification reads the tenant's events in sequence order;
            // this composite covers both the filter and the sort.
            entity.HasIndex(e => new { e.TenantId, e.SequenceNumber }, "idx_tenant_sequence");
        });

        modelBuilder.Entity<EngagementChainHead>(entity =>
        {
            entity.ToTable("engagement_chain_heads");

            entity.HasKey(h => h.EngagementId);

            entity.Property(h => h.EngagementId)
                .HasColumnName("engagement_id")
                .HasMaxLength(36);

            entity.Property(h => h.TenantId)
                .HasColumnName("tenant_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(h => h.LastEventId)
                .HasColumnName("last_event_id")
                .HasMaxLength(36);

            entity.Property(h => h.LastHash)
                .HasColumnName("last_hash")
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(h => h.UpdatedAt)
                .HasColumnName("updated_at")
                .IsRequired();
        });

        modelBuilder.Entity<AuditEventMetadata>(entity =>
        {
            entity.ToTable("audit_event_metadata");

            entity.HasKey(m => m.EventId);

            entity.Property(m => m.EventId)
                .HasColumnName("event_id")
                .HasMaxLength(36);

            entity.Property(m => m.TenantId)
                .HasColumnName("tenant_id")
                .HasMaxLength(36)
                .IsRequired();

            entity.Property(m => m.IsFlagged)
                .HasColumnName("is_flagged")
                .HasDefaultValue(false)
                .IsRequired();

            entity.Property(m => m.FlagReason)
                .HasColumnName("flag_reason")
                .HasMaxLength(500);

            entity.Property(m => m.FlaggedBy)
                .HasColumnName("flagged_by")
                .HasMaxLength(255);

            entity.Property(m => m.FlaggedAt)
                .HasColumnName("flagged_at");

            entity.Property(m => m.FlagReferenceEventId)
                .HasColumnName("flag_reference_event_id")
                .HasMaxLength(36);

            entity.Property(m => m.IsArchived)
                .HasColumnName("is_archived")
                .HasDefaultValue(false)
                .IsRequired();

            entity.Property(m => m.ArchiveReason)
                .HasColumnName("archive_reason")
                .HasMaxLength(500);

            entity.Property(m => m.ArchivedBy)
                .HasColumnName("archived_by")
                .HasMaxLength(255);

            entity.Property(m => m.ArchivedAt)
                .HasColumnName("archived_at");

            entity.Property(m => m.UpdatedAt)
                .HasColumnName("updated_at")
                .IsRequired();

            entity.HasOne(m => m.Event)
                .WithOne()
                .HasForeignKey<AuditEventMetadata>(m => m.EventId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(m => m.TenantId, "idx_metadata_tenant_id");
            entity.HasIndex(m => new { m.TenantId, m.EventId }, "idx_metadata_tenant_event");
        });
    }
}