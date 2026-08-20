using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Data;

public class GatherumDbContext(DbContextOptions<GatherumDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<FileBody> FileBodies => Set<FileBody>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<NodeCategory> NodeCategories => Set<NodeCategory>();
    public DbSet<NodeLink> NodeLinks => Set<NodeLink>();
    public DbSet<NodeEmbedding> NodeEmbeddings => Set<NodeEmbedding>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");

        model.Entity<Node>(node =>
        {
            node.Property(n => n.Title).HasMaxLength(500);
            node.Property(n => n.MediaType).HasMaxLength(255);
            node.Ignore(n => n.Kind);
            node.HasOne(n => n.Parent).WithMany(n => n.Children)
                .HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.Cascade);
            node.HasOne(n => n.Owner).WithMany().HasForeignKey(n => n.OwnerId);
            node.HasIndex(n => new { n.ParentId, n.Position });
            node.Property(n => n.SearchVector)
                .HasComputedColumnSql(
                    """
                    setweight(to_tsvector('english', coalesce("Title", '')), 'A') ||
                    setweight(to_tsvector('english', coalesce("SearchText", '')), 'B')
                    """,
                    stored: true);
            node.HasIndex(n => n.SearchVector).HasMethod("GIN");
            node.Property(n => n.TextFingerprint)
                .HasMaxLength(32)
                .HasComputedColumnSql(
                    """md5(coalesce("Title", '') || E'\n' || coalesce("SearchText", ''))""",
                    stored: true);
            node.Property(n => n.EmbeddedFingerprint).HasMaxLength(32);
            // The one predicate the embedding sweep runs: every node whose text has
            // moved on from what was embedded of it.
            node.HasIndex(n => new { n.EmbeddedFingerprint, n.TextFingerprint });
        });

        model.Entity<FileBody>(file =>
        {
            file.HasKey(f => f.NodeId);
            file.HasOne(f => f.Node).WithOne(n => n.File).HasForeignKey<FileBody>(f => f.NodeId);
            file.HasMany(f => f.Versions).WithOne().HasForeignKey(v => v.NodeId);
            file.Ignore(f => f.Current);
        });

        model.Entity<FileVersion>(version =>
        {
            version.Property(v => v.Hash).HasMaxLength(64);
            version.Property(v => v.MediaType).HasMaxLength(255);
            version.Property(v => v.FileName).HasMaxLength(500);
            version.HasIndex(v => new { v.NodeId, v.Number }).IsUnique();
        });

        model.Entity<Category>(category =>
        {
            category.Property(c => c.Name).HasMaxLength(CategoryPath.MaxSegmentLength);
            category.Property(c => c.Path).HasMaxLength(1000);
            category.HasIndex(c => c.Path).IsUnique();
            category.HasOne(c => c.Parent).WithMany(c => c.Children)
                .HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<NodeCategory>(membership =>
        {
            membership.HasKey(m => new { m.NodeId, m.CategoryId });
            membership.HasOne(m => m.Node).WithMany(n => n.Categories)
                .HasForeignKey(m => m.NodeId).OnDelete(DeleteBehavior.Cascade);
            membership.HasOne(m => m.Category).WithMany(c => c.Nodes)
                .HasForeignKey(m => m.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<NodeLink>(link =>
        {
            link.HasKey(l => new { l.SourceId, l.TargetId });
            link.HasOne(l => l.Source).WithMany(n => n.OutboundLinks)
                .HasForeignKey(l => l.SourceId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne(l => l.Target).WithMany(n => n.InboundLinks)
                .HasForeignKey(l => l.TargetId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<NodeEmbedding>(embedding =>
        {
            embedding.Property(e => e.Hash).HasMaxLength(64);
            embedding.Property(e => e.Model).HasMaxLength(200);
            // Dimensionless on purpose: the width is a runtime setting, applied to this
            // column (and to the index that needs it) by EmbeddingSchema at startup, so
            // changing embedding models is an env var rather than a migration.
            embedding.Property(e => e.Embedding).HasColumnType("vector");
            embedding.HasOne(e => e.Node).WithMany(n => n.Embeddings)
                .HasForeignKey(e => e.NodeId).OnDelete(DeleteBehavior.Cascade);
            embedding.HasIndex(e => new { e.NodeId, e.Ordinal });
            embedding.HasIndex(e => e.Hash);
        });

        model.Entity<User>(user =>
        {
            user.Property(u => u.Subject).HasMaxLength(255);
            user.Property(u => u.Email).HasMaxLength(320);
            user.Property(u => u.DisplayName).HasMaxLength(255);
            user.HasIndex(u => u.Subject).IsUnique();
        });

        model.Entity<ApiKey>(key =>
        {
            key.Property(k => k.Name).HasMaxLength(100);
            key.Property(k => k.KeyHash).HasMaxLength(64);
            key.Property(k => k.Prefix).HasMaxLength(12);
            key.HasIndex(k => k.KeyHash).IsUnique();
            key.Ignore(k => k.IsActive);
        });

    }
}
