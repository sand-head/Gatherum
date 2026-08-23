using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>The taxonomy: a tree of categories laid over the tree of nodes. A node has
/// exactly one place in the node tree but belongs to as many categories as its subject
/// demands, and a category holds everything in the categories nested under it. They are
/// addressed by path ("homelab/podman"), created by being used, and maintained —
/// renamed, moved, deleted — like the rest of the wiki. The taxonomy belongs to both
/// users: anyone who can see a node can file it, and there is no owner to ask.</summary>
public class CategoryService(GatherumDbContext db, NodeService nodes, INodeAuthorizer authorizer,
    NodeMetadataWriter sidecar)
{
    /// <summary>Files a node under a category, creating the category and everything it
    /// is nested under if they are new; returns the path it landed on. The written path
    /// decides the capitalization of the names it creates and nothing else: a category
    /// that already exists keeps its own.</summary>
    public async Task<string> AddAsync(Guid userId, Guid nodeId, string path,
        CancellationToken ct = default)
    {
        var segments = ValidSegments(path);
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        // Filing somebody's node under a subject changes what their node says it is
        // about, and it is written to their sidecar. That is a content change.
        nodes.EnsureEditable(node, userId);
        var category = await EnsureAsync(segments, ct);
        if (node.Categories.All(c => c.CategoryId != category.Id))
        {
            node.Categories.Add(new NodeCategory
            {
                NodeId = node.Id,
                CategoryId = category.Id,
                Category = category,
            });
            nodes.RefreshSearchText(node);
        }
        await db.SaveChangesAsync(ct);
        await sidecar.WriteAsync(nodeId, ct);
        return category.Path;
    }

    /// <summary>Takes a node out of one category. Nesting is untouched: a node in
    /// "homelab/podman" was never directly in "homelab", so removing it from there
    /// removes nothing.</summary>
    public async Task RemoveAsync(Guid userId, Guid nodeId, string path,
        CancellationToken ct = default)
    {
        var normalized = CategoryPath.Normalize(path);
        var node = await nodes.GetWithBodyAsync(userId, nodeId, ct);
        nodes.EnsureEditable(node, userId);
        var membership = node.Categories.FirstOrDefault(c => c.Category!.Path == normalized);
        if (membership is null)
            return;
        node.Categories.Remove(membership);
        db.NodeCategories.Remove(membership);
        nodes.RefreshSearchText(node);
        await db.SaveChangesAsync(ct);
        await sidecar.WriteAsync(nodeId, ct);
    }

    /// <summary>The whole taxonomy as a flat, path-ordered list; callers nest it. Counts
    /// are of what this user can see, and a category whose every member is private to
    /// the other user is left out entirely — a category name is a description of its
    /// members, so listing it would describe pages they can't see. An empty category is
    /// nobody's secret and stays. <paramref name="matching"/> filters the list after the
    /// counting, so a filtered row still knows its real size.</summary>
    public async Task<List<CategorySummary>> ListAsync(Guid? userId, string? matching = null,
        CancellationToken ct = default)
    {
        var categories = await db.Categories
            .OrderBy(c => c.Path)
            .Select(c => new { c.Id, c.Name, c.Path })
            .ToListAsync(ct);
        var visible = (await authorizer.VisibleTo(db.Nodes, userId).Select(n => n.Id)
            .ToListAsync(ct)).ToHashSet();
        var memberships = await db.NodeCategories
            .Select(m => new { m.NodeId, m.Category!.Path })
            .ToListAsync(ct);

        // A node can sit in a category and in one of its subcategories at once, so a
        // subtree's count is over distinct nodes, not a sum of its branches.
        HashSet<Guid> Subtree(string path, bool visibleOnly) => memberships
            .Where(m => m.Path == path || CategoryPath.IsDescendantOf(m.Path, path))
            .Where(m => !visibleOnly || visible.Contains(m.NodeId))
            .Select(m => m.NodeId)
            .ToHashSet();

        var filter = matching is { Length: > 0 } ? CategoryPath.Normalize(matching) : null;
        return categories
            .Select(c => new
            {
                c.Id, c.Name, c.Path,
                Members = memberships
                    .Where(m => m.Path == c.Path && visible.Contains(m.NodeId))
                    .Select(m => m.NodeId).Distinct().Count(),
                Visible = Subtree(c.Path, visibleOnly: true).Count,
                Total = Subtree(c.Path, visibleOnly: false).Count,
            })
            .Where(c => c.Visible > 0 || c.Total == 0)
            .Where(c => filter is null || c.Path.Contains(filter, StringComparison.Ordinal))
            .Select(c => new CategorySummary(c.Id, c.Name, c.Path, CategoryPath.Parent(c.Path),
                c.Members, c.Visible))
            .ToList();
    }

    /// <summary>One category as a page of the wiki: where it sits, what is nested under
    /// it, and what it holds — its own members, or the whole subtree's when
    /// <paramref name="deep"/> asks.</summary>
    public async Task<CategoryView> GetAsync(Guid? userId, string path, bool deep = false,
        CancellationToken ct = default)
    {
        var normalized = CategoryPath.Normalize(path);
        var all = (await ListAsync(userId, null, ct)).ToDictionary(c => c.Path);
        if (!all.TryGetValue(normalized, out var category))
            throw new NotFoundException($"Category '{normalized}' not found.");
        var ancestors = CategoryPath.Ancestry(normalized)
            .Where(p => p != normalized && all.ContainsKey(p))
            .Select(p => all[p])
            .ToList();
        var subcategories = all.Values.Where(c => c.ParentPath == normalized).ToList();
        return new CategoryView(category, ancestors, subcategories,
            await GetNodesAsync(userId, normalized, deep, ct));
    }

    /// <summary>The nodes in a category — its own members, plus its subcategories' when
    /// <paramref name="deep"/> asks, since a page about Podman is a page about the
    /// homelab.</summary>
    public Task<List<Node>> GetNodesAsync(Guid? userId, string path, bool deep = false,
        CancellationToken ct = default)
    {
        var normalized = CategoryPath.Normalize(path);
        var prefix = $"{normalized}{CategoryPath.Separator}";
        var visible = authorizer.VisibleTo(db.Nodes, userId);
        var members = deep
            ? visible.Where(n => n.Categories.Any(c =>
                c.Category!.Path == normalized || c.Category.Path.StartsWith(prefix)))
            : visible.Where(n => n.Categories.Any(c => c.Category!.Path == normalized));
        return members.OrderBy(n => n.Title).ToListAsync(ct);
    }

    /// <summary>Renames a category in place. Everything nested under it follows, because
    /// a subcategory's path is its parent's plus its own name.</summary>
    public async Task RenameAsync(string path, string name, CancellationToken ct = default)
    {
        var category = await RequireAsync(path, ct);
        if (ValidSegments(name) is not [var segment])
            throw new ValidationException("A category is renamed to one name, not a path.");
        category.Name = segment;
        await RepathAsync(category, CategoryPath.Parent(category.Path), ct);
    }

    /// <summary>Moves a category — and everything under it — beneath another one, or to
    /// the root when no new parent is named.</summary>
    public async Task MoveAsync(string path, string? newParentPath, CancellationToken ct = default)
    {
        var category = await RequireAsync(path, ct);
        var parent = newParentPath is { Length: > 0 } ? await RequireAsync(newParentPath, ct) : null;
        if (parent is not null &&
            (parent.Id == category.Id || CategoryPath.IsDescendantOf(parent.Path, category.Path)))
            throw new ForbiddenException("Cannot move a category into its own subtree.");
        category.ParentId = parent?.Id;
        await RepathAsync(category, parent?.Path, ct);
    }

    /// <summary>Deletes a category and everything nested under it. The nodes stay — they
    /// simply stop being about that subject.</summary>
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        var category = await RequireAsync(path, ct);
        var doomed = await SubtreeAsync(category, ct);
        var members = await MembersOfAsync(doomed, ct);
        db.Categories.RemoveRange(doomed);
        await db.SaveChangesAsync(ct);
        await RefreshSearchTextAsync(members, ct);
    }

    /// <summary>Walks the written path from the root, creating what isn't there yet.</summary>
    private async Task<Category> EnsureAsync(IReadOnlyList<string> segments, CancellationToken ct)
    {
        Category? category = null;
        var path = "";
        foreach (var segment in segments)
        {
            var parentId = category?.Id;
            path = path.Length == 0
                ? segment.ToLowerInvariant()
                : $"{path}{CategoryPath.Separator}{segment.ToLowerInvariant()}";
            category = await db.Categories.FirstOrDefaultAsync(c => c.Path == path, ct)
                ?? db.Categories.Add(new Category
                {
                    Id = Guid.NewGuid(),
                    Name = segment,
                    Path = path,
                    ParentId = parentId,
                }).Entity;
        }
        return category!;
    }

    private async Task<Category> RequireAsync(string path, CancellationToken ct)
    {
        var normalized = CategoryPath.Normalize(path);
        return await db.Categories.FirstOrDefaultAsync(c => c.Path == normalized, ct)
            ?? throw new NotFoundException($"Category '{normalized}' not found.");
    }

    /// <summary>Rewrites a category's path and its descendants' after a rename or a move,
    /// then refreshes the search text of every node underneath: a category contributes
    /// its whole ancestry to the text its members are found by.</summary>
    private async Task RepathAsync(Category category, string? parentPath, CancellationToken ct)
    {
        var subtree = await SubtreeAsync(category, ct);
        var members = await MembersOfAsync(subtree, ct);
        var oldPath = category.Path;
        var newPath = parentPath is { Length: > 0 }
            ? $"{parentPath}{CategoryPath.Separator}{category.Name.ToLowerInvariant()}"
            : category.Name.ToLowerInvariant();
        if (newPath != oldPath && await db.Categories.AnyAsync(c => c.Path == newPath, ct))
            throw new ValidationException($"A category '{newPath}' already exists.");

        category.Path = newPath;
        foreach (var descendant in subtree.Where(c => c.Id != category.Id))
            descendant.Path = newPath + descendant.Path[oldPath.Length..];
        await db.SaveChangesAsync(ct);
        await RefreshSearchTextAsync(members, ct);
    }

    private async Task<List<Category>> SubtreeAsync(Category category, CancellationToken ct)
    {
        var prefix = $"{category.Path}{CategoryPath.Separator}";
        var descendants = await db.Categories.Where(c => c.Path.StartsWith(prefix)).ToListAsync(ct);
        return [category, .. descendants];
    }

    private Task<List<Guid>> MembersOfAsync(IReadOnlyCollection<Category> categories,
        CancellationToken ct)
    {
        var ids = categories.Select(c => c.Id).ToList();
        return db.NodeCategories
            .Where(m => ids.Contains(m.CategoryId))
            .Select(m => m.NodeId)
            .Distinct()
            .ToListAsync(ct);
    }

    private async Task RefreshSearchTextAsync(IReadOnlyCollection<Guid> nodeIds,
        CancellationToken ct)
    {
        if (nodeIds.Count == 0)
            return;
        var affected = await db.Nodes
            .Where(n => nodeIds.Contains(n.Id))
            .Include(n => n.File!).ThenInclude(f => f.Versions)
            .Include(n => n.Categories).ThenInclude(c => c.Category)
            .ToListAsync(ct);
        foreach (var node in affected)
            nodes.RefreshSearchText(node);
        await db.SaveChangesAsync(ct);
    }

    private static List<string> ValidSegments(string path)
    {
        var segments = CategoryPath.Segments(path);
        if (segments.Count == 0)
            throw new ValidationException("A category needs a name.");
        if (segments.Count > CategoryPath.MaxDepth)
            throw new ValidationException($"Categories nest at most {CategoryPath.MaxDepth} deep.");
        if (segments.Any(segment => segment.Length > CategoryPath.MaxSegmentLength))
            throw new ValidationException(
                $"A category name is at most {CategoryPath.MaxSegmentLength} characters.");
        return segments;
    }
}

/// <summary>A category and how much it holds: <c>Members</c> is what sits in it
/// directly, <c>SubtreeMembers</c> counts the subcategories' too — both of them only
/// what the asking user is allowed to see.</summary>
public record CategorySummary(Guid Id, string Name, string Path, string? ParentPath,
    int Members, int SubtreeMembers);

public record CategoryView(CategorySummary Category, IReadOnlyList<CategorySummary> Ancestors,
    IReadOnlyList<CategorySummary> Subcategories, IReadOnlyList<Node> Nodes);
