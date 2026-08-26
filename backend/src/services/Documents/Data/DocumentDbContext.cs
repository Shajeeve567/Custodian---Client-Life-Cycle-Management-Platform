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
            entity.HasKey(e => e.DocumentId);

            entity.Property(e => e.EngagementId)
                .IsRequired();

            entity.Property(e => e.TenantId)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.Type)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.UploaderId)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.FileName)
                .IsRequired()
                .HasMaxLength(255);

            entity.Property(e => e.ContentType)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.StoragePath)
                .IsRequired()
                .HasMaxLength(500);

            entity.HasIndex(e => e.TenantId);
            entity.HasIndex(e => e.EngagementId);
            entity.HasIndex(e => e.Type);
        });
    }
}
