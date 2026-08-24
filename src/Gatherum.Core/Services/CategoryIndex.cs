using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>The whole taxonomy, in memory, for the length of one operation.
///
/// A category used to carry its ancestry in its path, so "everything under Homelab" was a
/// string prefix and cost nothing. A category is a node now and nesting is an edge, so the
/// same question is a graph walk — and this is where it is walked. Loading the lot is the
/// right shape for it: the taxonomy is a table of dozens of rows in a wiki this size, the
/// alternative is a recursive query per question, and <see cref="NodeService.GetSimilarAsync"/>
/// already decided this exact trade for exactly this reason.
///
/// It is a snapshot. Load one per operation and let it go; nothing here notices a write.</summary>
public sealed class CategoryIndex
{
    private readonly Dictionary<Guid, string> names;

    /// <summary>Category → the categories it is filed under. A category can be filed under
    /// more than one, so this is a graph and not a tree: "Podman" may be a subcategory of
    /// "Homelab" and of "Containers" at once.</summary>
    private readonly Dictionary<Guid, List<Guid>> parents;
    private readonly Dictionary<Guid, List<Guid>> children;

    private CategoryIndex(Dictionary<Guid, string> names,
        Dictionary<Guid, List<Guid>> parents, Dictionary<Guid, List<Guid>> children)
    {
        this.names = names;
        this.parents = parents;
        this.children = children;
    }

    public static async Task<CategoryIndex> LoadAsync(GatherumDbContext db,
        CancellationToken ct = default)
    {
        var names = await db.Nodes.Where(n => n.IsCategory)
            .Select(n => new { n.Id, n.Title })
            .ToDictionaryAsync(n => n.Id, n => n.Title, ct);
        var parents = names.Keys.ToDictionary(id => id, _ => new List<Guid>());
        var children = names.Keys.ToDictionary(id => id, _ => new List<Guid>());
        // Only the edges between two categories are nesting; the rest are memberships,
        // and a page under Homelab is not a subcategory of it.
        var edges = await db.NodeCategories
            .Where(m => m.Node!.IsCategory)
            .Select(m => new { m.NodeId, m.CategoryId })
            .ToListAsync(ct);
        foreach (var edge in edges)
        {
            if (!parents.TryGetValue(edge.NodeId, out var up)
                || !children.TryGetValue(edge.CategoryId, out var down))
                continue;
            up.Add(edge.CategoryId);
            down.Add(edge.NodeId);
        }
        return new CategoryIndex(names, parents, children);
    }

    public IReadOnlyCollection<Guid> Ids => names.Keys;

    public bool Knows(Guid categoryId) => names.ContainsKey(categoryId);

    public string NameOf(Guid categoryId) => names.GetValueOrDefault(categoryId, "");

    public IReadOnlyList<Guid> ParentsOf(Guid categoryId) =>
        parents.GetValueOrDefault(categoryId) ?? [];

    public IReadOnlyList<Guid> ChildrenOf(Guid categoryId) =>
        children.GetValueOrDefault(categoryId) ?? [];

    /// <summary>The categories with nothing above them — where a reader starts.</summary>
    public IEnumerable<Guid> Roots() => names.Keys.Where(id => ParentsOf(id).Count == 0);

    /// <summary>Everything a category is nested under, however far up, not including
    /// itself. Cycles cannot be filed, but a snapshot taken mid-repair might hold one, so
    /// the walk is guarded rather than trusting.</summary>
    public HashSet<Guid> AncestorsOf(Guid categoryId) => Reach(categoryId, ParentsOf);

    /// <summary>Everything nested under a category, however far down, not including
    /// itself.</summary>
    public HashSet<Guid> DescendantsOf(Guid categoryId) => Reach(categoryId, ChildrenOf);

    /// <summary>A category and everything under it — what "the subtree of Homelab" meant
    /// when a path could say it.</summary>
    public HashSet<Guid> SubtreeOf(Guid categoryId)
    {
        var subtree = DescendantsOf(categoryId);
        subtree.Add(categoryId);
        return subtree;
    }

    /// <summary>The categories a node is really in: the ones it was filed under, plus
    /// everything those are nested under, because a page about Podman is a page about the
    /// homelab.</summary>
    public HashSet<Guid> ClosureOf(IEnumerable<Guid> categoryIds)
    {
        var closure = new HashSet<Guid>();
        foreach (var id in categoryIds)
        {
            if (!names.ContainsKey(id) || !closure.Add(id))
                continue;
            closure.UnionWith(AncestorsOf(id));
        }
        return closure;
    }

    /// <summary>What a node's categories contribute to the text it is found by: their
    /// names and every name they are nested under, so searching "homelab" finds the page
    /// filed only under "Podman". Ordered shallowest first so the words read the way the
    /// taxonomy does and the same set always spells the same string.</summary>
    public string Words(IEnumerable<Guid> categoryIds) => string.Join(' ', ClosureOf(categoryIds)
        .Select(id => new { Depth = AncestorsOf(id).Count, Name = names[id] })
        .OrderBy(c => c.Depth).ThenBy(c => c.Name, StringComparer.Ordinal)
        .Select(c => c.Name.ToLowerInvariant()));

    private HashSet<Guid> Reach(Guid start, Func<Guid, IReadOnlyList<Guid>> step)
    {
        var found = new HashSet<Guid>();
        var queue = new Queue<Guid>(step(start));
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (next == start || !found.Add(next))
                continue;
            foreach (var further in step(next))
                queue.Enqueue(further);
        }
        return found;
    }
}
