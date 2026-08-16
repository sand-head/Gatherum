using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Data;

public class GatherumDbContext(DbContextOptions<GatherumDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<PageBody> PageBodies => Set<PageBody>();
    public DbSet<FileBody> FileBodies => Set<FileBody>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<Revision> Revisions => Set<Revision>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<NodeTag> NodeTags => Set<NodeTag>();
    public DbSet<NodeLink> NodeLinks => Set<NodeLink>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<YjsDoc> YjsDocs => Set<YjsDoc>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Node>(node =>
        {
            node.Property(n => n.Title).HasMaxLength(500);
            node.Property(n => n.Kind).HasConversion<string>().HasMaxLength(10);
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
        });

        model.Entity<PageBody>(page =>
        {
            page.HasKey(p => p.NodeId);
            page.HasOne(p => p.Node).WithOne(n => n.Page).HasForeignKey<PageBody>(p => p.NodeId);
            page.Property(p => p.Doc).HasColumnType("jsonb");
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

        model.Entity<Revision>(revision =>
        {
            revision.Property(r => r.Title).HasMaxLength(500);
            revision.Property(r => r.Doc).HasColumnType("jsonb");
            revision.HasIndex(r => new { r.NodeId, r.Number }).IsUnique();
            revision.HasOne<Node>().WithMany(n => n.Revisions)
                .HasForeignKey(r => r.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Tag>(tag =>
        {
            tag.Property(t => t.Name).HasMaxLength(100);
            tag.HasIndex(t => t.Name).IsUnique();
        });

        model.Entity<NodeTag>(nodeTag =>
        {
            nodeTag.HasKey(nt => new { nt.NodeId, nt.TagId });
            nodeTag.HasOne(nt => nt.Node).WithMany(n => n.Tags)
                .HasForeignKey(nt => nt.NodeId).OnDelete(DeleteBehavior.Cascade);
            nodeTag.HasOne(nt => nt.Tag).WithMany(t => t.Nodes)
                .HasForeignKey(nt => nt.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<NodeLink>(link =>
        {
            link.HasKey(l => new { l.SourceId, l.TargetId });
            link.HasOne(l => l.Source).WithMany(n => n.OutboundLinks)
                .HasForeignKey(l => l.SourceId).OnDelete(DeleteBehavior.Cascade);
            link.HasOne(l => l.Target).WithMany(n => n.InboundLinks)
                .HasForeignKey(l => l.TargetId).OnDelete(DeleteBehavior.Cascade);
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

        model.Entity<YjsDoc>(doc =>
        {
            doc.HasKey(d => d.NodeId);
            doc.HasOne(d => d.Node).WithOne().HasForeignKey<YjsDoc>(d => d.NodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
