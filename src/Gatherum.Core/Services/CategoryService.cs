using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>The taxonomy: what the wiki is about, arranged. A category is a page —
/// an ordinary Markdown node with <see cref="Node.IsCategory"/> set — so it has a body
/// saying what belongs in it, a version history, backlinks, and a <c>[[Homelab]]</c> that
/// resolves to it. Filing a page under a category is an edge; filing a <em>category</em>
/// under a category is the same edge, and that is what a subcategory is.
///
/// Nothing here renames, re-nests or deletes a category, because there is nothing left for
/// it to do: a category is renamed by renaming its page, re-nested by filing it somewhere
/// else, and deleted by deleting it. That collapse is the point of the whole design — the
/// old model had a parallel set of verbs because a category was a path, and a path is a
/// thing only its own service can maintain.
///
/// The taxonomy still belongs to both users in the sense that mattered: anyone who can
/// edit a node can file it under anything. What is new is that a category page is a page,
/// so who may read its lede and who may edit it are that page's own business.</summary>
public class CategoryService(GatherumDbContext db, NodeService nodes, FileService files,
    INodeAuthorizer authorizer, NodeMetadataWriter sidecar, TimeProvider clock)
{
    /// <summary>Where a category page is created when nobody has written one yet. A
    /// directory rather than the root of the tree, because a wiki accumulates far more
    /// subjects than top-level pages, and because somebody reading these directories with
    /// no Gatherum running should be able to see what the taxonomy is at a glance.</summary>
    public const string Folder = "Categories";

    /// <summary>Files a node under a category, writing the category's page if this is the
    /// first anyone has mentioned it; returns the name it landed on. The written name
    /// decides the capitalization only when it creates the category — one that already
    /// exists keeps its own, because its page is called that.</summary>
    public async Task<string> AddAsync(Guid userId, Guid nodeId, string? name,
        CancellationToken ct = default)
    {
        var wanted = ValidName(name);
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        // Filing somebody's node under a subject changes what their node says it is
        // about, and it is written to their sidecar. That is a content change.
        nodes.EnsureEditable(node, userId);
        var category = await EnsureAsync(userId, wanted, ct);

        var taxonomy = await CategoryIndex.LoadAsync(db, ct);
        if (category.Id == node.Id || (node.IsCategory && taxonomy.AncestorsOf(category.Id)
            .Contains(node.Id)))
            throw new ForbiddenException("A category cannot be nested inside itself.");

        if (node.Categories.All(c => c.CategoryId != category.Id))
        {
            node.Categories.Add(new NodeCategory
            {
                NodeId = node.Id,
                CategoryId = category.Id,
                Category = category,
            });
            await db.SaveChangesAsync(ct);
            await nodes.RefreshCategoryReachAsync(node, ct);
        }
        await sidecar.WriteAsync(nodeId, ct);
        return category.Title;
    }

    /// <summary>Takes a node out of one category. Nesting is untouched: a node in "Podman"
    /// was never directly in "Homelab", so removing it from there removes nothing.</summary>
    public async Task RemoveAsync(Guid userId, Guid nodeId, string name,
        CancellationToken ct = default)
    {
        var key = CategoryName.Key(name);
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        nodes.EnsureEditable(node, userId);
        var membership = node.Categories
            .FirstOrDefault(c => CategoryName.Key(c.Category!.Title) == key);
        if (membership is null)
            return;
        node.Categories.Remove(membership);
        db.NodeCategories.Remove(membership);
        await db.SaveChangesAsync(ct);
        await nodes.RefreshCategoryReachAsync(node, ct);
        await sidecar.WriteAsync(nodeId, ct);
    }

    /// <summary>The category a name refers to, or null. Names are unique among categories
    /// and spelled forgivingly, which is what lets a sidecar on disk say "Podman" and mean
    /// something without a database to look the id up in.</summary>
    public Task<Node?> ResolveAsync(string name, CancellationToken ct = default)
    {
        var key = CategoryName.Key(name);
        return db.Nodes.FirstOrDefaultAsync(n => n.IsCategory && n.Title.ToLower() == key, ct);
    }

    /// <summary>The whole taxonomy, by name; callers nest it through
    /// <see cref="CategorySummary.ParentIds"/>. Counts are of what this user can see, and a
    /// category whose every member is private to somebody else is left out entirely — the
    /// name of a category describes the pages in it, so listing it would describe pages
    /// they can't see. An empty category is nobody's secret and stays.
    /// <paramref name="matching"/> filters after the counting, so a filtered row still
    /// knows its real size.</summary>
    public async Task<List<CategorySummary>> ListAsync(Guid? userId, string? matching = null,
        CancellationToken ct = default)
    {
        var taxonomy = await CategoryIndex.LoadAsync(db, ct);
        var visible = (await authorizer.VisibleTo(db.Nodes, userId).Select(n => n.Id)
            .ToListAsync(ct)).ToHashSet();
        // Subcategories are listed as subcategories, never counted as members: a category
        // page is a node, and without this every parent would report its children twice.
        var memberships = await db.NodeCategories
            .Where(m => !m.Node!.IsCategory)
            .Select(m => new { m.NodeId, m.CategoryId })
            .ToListAsync(ct);
        var byCategory = memberships.ToLookup(m => m.CategoryId, m => m.NodeId);

        // A node can sit in a category and in one of its subcategories at once, so a
        // subtree's count is over distinct nodes, not a sum of its branches.
        HashSet<Guid> Subtree(Guid id, bool visibleOnly) => taxonomy.SubtreeOf(id)
            .SelectMany(c => byCategory[c])
            .Where(nodeId => !visibleOnly || visible.Contains(nodeId))
            .ToHashSet();

        var filter = matching is { Length: > 0 } ? CategoryName.Key(matching) : null;
        return taxonomy.Ids
            .Select(id => new
            {
                Id = id,
                Name = taxonomy.NameOf(id),
                Members = byCategory[id].Where(visible.Contains).Distinct().Count(),
                Visible = Subtree(id, visibleOnly: true).Count,
                Total = Subtree(id, visibleOnly: false).Count,
            })
            .Where(c => c.Visible > 0 || c.Total == 0)
            .Where(c => filter is null
                || CategoryName.Key(c.Name).Contains(filter, StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new CategorySummary(c.Id, c.Name, taxonomy.ParentsOf(c.Id).ToList(),
                c.Members, c.Visible))
            .ToList();
    }

    /// <summary>One category: the categories it is nested under, what is nested under it,
    /// and what it holds — its own members, or the whole subtree's when
    /// <paramref name="deep"/> asks. A category can sit under more than one parent, so
    /// this reports parents rather than a single chain up to a root; there isn't one.</summary>
    public async Task<CategoryView> GetAsync(Guid? userId, string name, bool deep = false,
        CancellationToken ct = default)
    {
        var all = (await ListAsync(userId, null, ct)).ToDictionary(c => c.Id);
        var key = CategoryName.Key(name);
        var category = all.Values.FirstOrDefault(c => CategoryName.Key(c.Name) == key)
            ?? throw new NotFoundException($"Category '{name}' not found.");
        var parents = category.ParentIds.Where(all.ContainsKey).Select(id => all[id]).ToList();
        var subcategories = all.Values.Where(c => c.ParentIds.Contains(category.Id)).ToList();
        return new CategoryView(category, parents, subcategories,
            await GetNodesAsync(userId, category.Id, deep, ct));
    }

    /// <summary>The nodes in a category — its own members, plus its subcategories' when
    /// <paramref name="deep"/> asks, since a page about Podman is a page about the
    /// homelab. Subcategories themselves are not members and are never in this list.</summary>
    public async Task<List<Node>> GetNodesAsync(Guid? userId, Guid categoryId, bool deep = false,
        CancellationToken ct = default)
    {
        var taxonomy = await CategoryIndex.LoadAsync(db, ct);
        var wanted = (deep ? taxonomy.SubtreeOf(categoryId) : [categoryId]).ToList();
        return await authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => !n.IsCategory && n.Categories.Any(c => wanted.Contains(c.CategoryId)))
            .OrderBy(n => n.Title)
            .ToListAsync(ct);
    }

    /// <summary>The category page for a name, written if nobody has written one. Creating
    /// it is an ordinary page creation — that is the whole claim of this design — so it
    /// lands in the given user's own root, private until they say otherwise, with an empty
    /// body waiting for somebody to say what belongs in it.
    ///
    /// Public because the reindex needs it too: a name in a <c>meta.json</c> that nothing
    /// on disk answers to has to become a page, and there should be exactly one piece of
    /// code that decides where a category page goes.</summary>
    public async Task<Node> EnsureAsync(Guid userId, string name, CancellationToken ct = default)
    {
        if (await ResolveAsync(name, ct) is { } existing)
            return existing;
        // Only a category answers to a category name: an ordinary page called "Podman"
        // is not quietly promoted into a subject because somebody filed something under
        // that word. Two nodes end up sharing a title, which they always could.
        var folder = await FolderAsync(userId, ct);
        var page = await files.CreateTextNodeAsync(userId, folder.Id, name, "",
            MediaTypes.Markdown, ct);
        page.IsCategory = true;
        await db.SaveChangesAsync(ct);
        await sidecar.WriteAsync(page.Id, ct);
        return page;
    }

    /// <summary>The <c>Categories/</c> directory in a user's root, made on first use. The
    /// reindex finds it again by its path like any other directory node.</summary>
    private async Task<Node> FolderAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.Nodes
            .FirstOrDefaultAsync(n => n.OwnerId == userId && n.RelativePath == Folder, ct);
        if (existing is not null)
            return existing;
        var now = clock.GetUtcNow();
        var folder = new Node
        {
            Id = Guid.NewGuid(),
            Title = Folder,
            MediaType = MediaTypes.Directory,
            OwnerId = userId,
            RelativePath = Folder,
            Position = await db.Nodes.CountAsync(n => n.OwnerId == userId && n.ParentId == null, ct),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Nodes.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    private static string ValidName(string? name)
    {
        var collapsed = CategoryName.Collapse(name ?? "");
        if (collapsed.Length == 0)
            throw new ValidationException("A category needs a name.");
        if (collapsed.Length > CategoryName.MaxLength)
            throw new ValidationException(
                $"A category name is at most {CategoryName.MaxLength} characters.");
        return collapsed;
    }
}

/// <summary>A category and how much it holds: <c>Members</c> is what sits in it directly,
/// <c>SubtreeMembers</c> counts the subcategories' too — both of them only what the asking
/// user is allowed to see, and neither of them counting the subcategories themselves.
/// <c>ParentIds</c> is a list because a subject can belong under more than one.</summary>
public record CategorySummary(Guid Id, string Name, IReadOnlyList<Guid> ParentIds,
    int Members, int SubtreeMembers);

public record CategoryView(CategorySummary Category, IReadOnlyList<CategorySummary> Parents,
    IReadOnlyList<CategorySummary> Subcategories, IReadOnlyList<Node> Nodes);
