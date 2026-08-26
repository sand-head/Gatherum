using Gatherum.Core.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Data;

public class GatherumDbContext(DbContextOptions<GatherumDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<FileBody> FileBodies => Set<FileBody>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<NodeCategory> NodeCategories => Set<NodeCategory>();
    public DbSet<NodeLink> NodeLinks => Set<NodeLink>();
    public DbSet<NodeGrant> NodeGrants => Set<NodeGrant>();
    public DbSet<NodeAccessEntry> NodeAccessEntries => Set<NodeAccessEntry>();
    public DbSet<NodeEmbedding> NodeEmbeddings => Set<NodeEmbedding>();
    public DbSet<ReadingPosition> ReadingPositions => Set<ReadingPosition>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    /// <summary>The keys protecting sign-in cookies. They belong here rather than on disk
    /// for the same reason <see cref="Users"/> and <see cref="ApiKeys"/> do: they are not
    /// derived from the directories, and losing them costs exactly one thing — signing in
    /// again — which is what losing the users costs anyway.
    ///
    /// Keeping them out of the storage root also keeps them out of the backup somebody is
    /// encouraged to take of it. That directory is meant to be browsed, rsynced and
    /// copied around; cookie-signing key material should not ride along.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasPostgresExtension("vector");

        model.Entity<Node>(node =>
        {
            node.Property(n => n.Title).HasMaxLength(500);
            node.Property(n => n.MediaType).HasMaxLength(255);
            node.Ignore(n => n.Kind);
            // Every taxonomy read starts by pulling the categories, so they are worth
            // finding without a scan even in a wiki this size.
            node.HasIndex(n => n.IsCategory);
            node.HasOne(n => n.Parent).WithMany(n => n.Children)
                .HasForeignKey(n => n.ParentId).OnDelete(DeleteBehavior.Cascade);
            node.HasOne(n => n.Owner).WithMany().HasForeignKey(n => n.OwnerId);
            node.HasIndex(n => new { n.ParentId, n.Position });
            node.Property(n => n.RelativePath).HasMaxLength(1024);
            // Ownership is the path, so a path is unique within the root that owns it.
            node.HasIndex(n => new { n.OwnerId, n.RelativePath });
            // What every visibility query filters on, for both of its questions.
            node.HasIndex(n => n.Reach);
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

        // The taxonomy: one self-referencing many-to-many over Nodes. A membership and a
        // nesting are the same row, which is why there is no Categories table left to map.
        model.Entity<NodeCategory>(membership =>
        {
            membership.HasKey(m => new { m.NodeId, m.CategoryId });
            membership.HasOne(m => m.Node).WithMany(n => n.Categories)
                .HasForeignKey(m => m.NodeId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a category page unfiles everything that was in it and leaves the
            // pages alone, which is what deleting a category has always meant here.
            membership.HasOne(m => m.Category).WithMany(n => n.Members)
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

        model.Entity<NodeGrant>(grant =>
        {
            grant.HasKey(g => new { g.NodeId, g.UserId });
            grant.HasOne(g => g.Node).WithMany(n => n.Grants)
                .HasForeignKey(g => g.NodeId).OnDelete(DeleteBehavior.Cascade);
            grant.HasOne(g => g.User).WithMany()
                .HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<NodeAccessEntry>(entry =>
        {
            entry.HasKey(e => new { e.NodeId, e.UserId });
            entry.HasOne(e => e.Node).WithMany(n => n.AccessEntries)
                .HasForeignKey(e => e.NodeId).OnDelete(DeleteBehavior.Cascade);
            // The join every signed-in visibility check makes.
            entry.HasIndex(e => e.UserId);
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

        model.Entity<ReadingPosition>(position =>
        {
            position.HasKey(p => new { p.NodeId, p.UserId });
            position.HasOne(p => p.Node).WithMany()
                .HasForeignKey(p => p.NodeId).OnDelete(DeleteBehavior.Cascade);
            position.HasOne(p => p.User).WithMany()
                .HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
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
