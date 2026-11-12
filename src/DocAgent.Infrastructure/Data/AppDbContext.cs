using DocAgent.Core.Abstractions;
using DocAgent.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DocAgent.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser? currentUser = null)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    /// <summary>
    /// Read by the global query filters. EF re-evaluates it per query on the context instance,
    /// so each request's context filters by that request's user. Null (anonymous) matches no rows.
    /// </summary>
    public Guid? CurrentUserId => currentUser?.UserId;

    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Chunk> Chunks => Set<Chunk>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    public DbSet<DocumentUpload> DocumentUploads => Set<DocumentUpload>();
    public DbSet<UserLlmCredential> UserLlmCredentials => Set<UserLlmCredential>();
    public DbSet<ExternalLoginCode> ExternalLoginCodes => Set<ExternalLoginCode>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Document>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(512);
            e.Property(x => x.ContentType).HasMaxLength(255);
            e.Property(x => x.ExtractionNotice).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        modelBuilder.Entity<Chunk>(e =>
        {
            e.Property(x => x.EmbeddingModel).HasMaxLength(128);
            e.Property(x => x.Embedding).HasColumnType($"vector({Chunk.EmbeddingDimensions})");
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => new { x.DocumentId, x.Ordinal });
            e.HasIndex(x => x.Embedding)
                .HasMethod("hnsw")
                .HasOperators("vector_cosine_ops");
            // Full-text search for hybrid retrieval. 'simple' (no stemming, no stop words) works for Arabic and English alike.
            e.HasIndex(x => x.Text)
                .HasDatabaseName("IX_Chunks_Text_fts")
                .HasMethod("gin")
                .IsTsVectorExpressionIndex("simple");
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        modelBuilder.Entity<IngestionJob>(e =>
        {
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            e.HasOne<Document>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.DocumentId);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        // Staging bytes (SRS D15). No query filter on purpose: it has no UserId and is only accessed by the
        // ingestion worker, always by DocumentId. Request paths never query it.
        modelBuilder.Entity<DocumentUpload>(e =>
        {
            e.HasKey(x => x.DocumentId);
            e.HasOne<Document>().WithOne().HasForeignKey<DocumentUpload>(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserLlmCredential>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.Provider).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Model).HasMaxLength(256);
            e.Property(x => x.KeyLast4).HasMaxLength(8);
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserLlmCredential>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => x.UserId == CurrentUserId);
        });

        modelBuilder.Entity<ExternalLoginCode>(e =>
        {
            e.Property(x => x.CodeHash).HasMaxLength(64);
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
