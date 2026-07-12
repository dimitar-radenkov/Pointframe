using Microsoft.EntityFrameworkCore;
using Pointframe.Data.Entities;

namespace Pointframe.Data.Context;

public sealed class PointframeDataContext : DbContext
{
    public PointframeDataContext(DbContextOptions<PointframeDataContext> options)
        : base(options)
    {
    }

    public DbSet<CaptureTextCacheEntry> CaptureTextCacheEntries => Set<CaptureTextCacheEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CaptureTextCacheEntry>();
        entity.ToTable("capture_text_cache");
        entity.HasKey(entry => entry.Id);
        entity.Property(entry => entry.Id)
            .HasColumnName("id")
            .IsRequired();
        entity.Property(entry => entry.FilePath)
            .HasColumnName("file_path")
            .IsRequired();
        entity.Property(entry => entry.CapturedAt)
            .HasColumnName("captured_at")
            .IsRequired();
        entity.Property(entry => entry.Text)
            .HasColumnName("text");
        entity.Property(entry => entry.LastAccessedAt)
            .HasColumnName("last_accessed_at")
            .IsRequired();
        entity.Property(entry => entry.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();
        entity.Property(entry => entry.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();
        entity.HasIndex(entry => entry.FilePath)
            .IsUnique()
            .HasDatabaseName("ix_capture_text_cache_file_path");
        entity.HasIndex(entry => entry.LastAccessedAt)
            .HasDatabaseName("ix_capture_text_cache_last_accessed_at");
    }
}
