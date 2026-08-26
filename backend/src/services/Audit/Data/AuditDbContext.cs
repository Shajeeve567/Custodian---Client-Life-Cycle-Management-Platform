using Custodian.Audit.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Audit.Data;

public class AuditDbContext : DbContext
{
    public AuditDbContext(DbContextOptions<AuditDbContext> options) : base(options)
    {
    }

    public DbSet<AuditEvent> Events { get; set; } = null!;

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

            entity.Property(e => e.Payload)
                .HasColumnName("payload")
                .HasColumnType("json")
                .IsRequired();

            entity.Property(e => e.SequenceNumber)
                .HasColumnName("sequence_number")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.Hash)
                .HasColumnName("hash")
                .HasMaxLength(64);

            // Performance Indexes
            entity.HasIndex(e => e.EngagementId, "idx_engagement_id");
            entity.HasIndex(e => e.TenantId, "idx_tenant_id");
            entity.HasIndex(e => new { e.TenantId, e.EngagementId }, "idx_tenant_engagement");
            entity.HasIndex(e => e.Type, "idx_type");
        });
    }
}
