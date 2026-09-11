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

    public DbSet<CaptureArtifactEntry> CaptureArtifacts => Set<CaptureArtifactEntry>();

    public DbSet<CaptureLocationEntry> CaptureLocations => Set<CaptureLocationEntry>();

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

        var artifact = modelBuilder.Entity<CaptureArtifactEntry>();
        artifact.ToTable("capture_artifacts");
        artifact.HasKey(entry => entry.ArtifactId);
        artifact.Property(entry => entry.ArtifactId).HasColumnName("artifact_id");
        artifact.Property(entry => entry.Kind).HasColumnName("kind").IsRequired();
        artifact.Property(entry => entry.MimeType).HasColumnName("mime_type").IsRequired();
        artifact.Property(entry => entry.FileName).HasColumnName("file_name").IsRequired();
        artifact.Property(entry => entry.Sha256).HasColumnName("sha256").IsRequired();
        artifact.Property(entry => entry.ByteLength).HasColumnName("byte_length");
        artifact.Property(entry => entry.PixelWidth).HasColumnName("pixel_width");
        artifact.Property(entry => entry.PixelHeight).HasColumnName("pixel_height");
        artifact.Property(entry => entry.CapturedAtUtc).HasColumnName("captured_at_utc").IsRequired();
        artifact.Property(entry => entry.TimestampSource).HasColumnName("timestamp_source").HasConversion<string>().IsRequired();
        artifact.Property(entry => entry.Source).HasColumnName("source").HasConversion<string>().IsRequired();
        artifact.Property(entry => entry.ProvenanceJson).HasColumnName("provenance_json");
        artifact.Property(entry => entry.Availability).HasColumnName("availability").HasConversion<string>().IsRequired();
        artifact.Property(entry => entry.OcrStatus).HasColumnName("ocr_status").HasConversion<string>().IsRequired();
        artifact.Property(entry => entry.OcrText).HasColumnName("ocr_text");
        artifact.Property(entry => entry.SearchTextNormalized).HasColumnName("search_text_normalized");
        artifact.Property(entry => entry.IndexedSha256).HasColumnName("indexed_sha256");
        artifact.Property(entry => entry.OcrEngineVersion).HasColumnName("ocr_engine_version");
        artifact.Property(entry => entry.IndexedAtUtc).HasColumnName("indexed_at_utc");
        artifact.Property(entry => entry.AttemptCount).HasColumnName("attempt_count");
        artifact.Property(entry => entry.NextAttemptUtc).HasColumnName("next_attempt_utc");
        artifact.Property(entry => entry.LeaseOwner).HasColumnName("lease_owner");
        artifact.Property(entry => entry.LeaseExpiresUtc).HasColumnName("lease_expires_utc");
        artifact.Property(entry => entry.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        artifact.Property(entry => entry.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        artifact.HasIndex(entry => new { entry.CapturedAtUtc, entry.ArtifactId })
            .HasDatabaseName("ix_capture_artifacts_captured_at_utc_artifact_id");
        artifact.HasIndex(entry => entry.Availability)
            .HasDatabaseName("ix_capture_artifacts_availability");
        artifact.HasIndex(entry => new { entry.OcrStatus, entry.NextAttemptUtc })
            .HasDatabaseName("ix_capture_artifacts_ocr_work");

        var location = modelBuilder.Entity<CaptureLocationEntry>();
        location.ToTable("capture_locations");
        location.HasKey(entry => entry.NormalizedPath);
        location.Property(entry => entry.NormalizedPath).HasColumnName("normalized_path");
        location.Property(entry => entry.OriginalPath).HasColumnName("original_path").IsRequired();
        location.Property(entry => entry.CurrentArtifactId).HasColumnName("current_artifact_id");
        location.Property(entry => entry.LastWriteAtUtc).HasColumnName("last_write_at_utc").IsRequired();
        location.Property(entry => entry.ByteLength).HasColumnName("byte_length");
        location.Property(entry => entry.LastObservedAtUtc).HasColumnName("last_observed_at_utc").IsRequired();
        location.HasOne(entry => entry.CurrentArtifact)
            .WithMany(entry => entry.Locations)
            .HasForeignKey(entry => entry.CurrentArtifactId)
            .OnDelete(DeleteBehavior.Restrict);
        location.HasIndex(entry => entry.CurrentArtifactId)
            .HasDatabaseName("ix_capture_locations_current_artifact_id");
    }
}
