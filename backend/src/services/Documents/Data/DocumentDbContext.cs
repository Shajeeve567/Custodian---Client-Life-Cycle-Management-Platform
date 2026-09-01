using Custodian.Documents.Models;
using Microsoft.EntityFrameworkCore;

namespace Custodian.Documents.Data;

public class DocumentDbContext : DbContext
{
    public DocumentDbContext(DbContextOptions<DocumentDbContext> options) : base(options)
    {
    }

    public DbSet<DocumentMetadata> Documents => Set<DocumentMetadata>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<DocumentMetadata>(entity =>
        {
            entity.ToTable("documents");

            entity.HasKey(e => e.DocumentId);

            entity.Property(e => e.DocumentId)
                .HasColumnName("document_id");

            entity.Property(e => e.EngagementId)
                .HasColumnName("engagement_id")
                .IsRequired();

            entity.Property(e => e.TenantId)
                .HasColumnName("tenant_id")
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.Type)
                .HasColumnName("type")
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.IssueDate)
                .HasColumnName("issue_date");

            entity.Property(e => e.ExpiryDate)
                .HasColumnName("expiry_date");

            entity.Property(e => e.UploaderId)
                .HasColumnName("uploader_id")
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.FileName)
                .HasColumnName("file_name")
                .IsRequired()
                .HasMaxLength(255);

            entity.Property(e => e.ContentType)
                .HasColumnName("content_type")
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.FileSize)
                .HasColumnName("file_size");

            entity.Property(e => e.StoragePath)
                .HasColumnName("storage_path")
                .IsRequired()
                .HasMaxLength(500);

            entity.Property(e => e.UploadedAt)
                .HasColumnName("created_at")
                .IsRequired();

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.EngagementId);
            entity.HasIndex(e => e.Type);
        });
    }
}
