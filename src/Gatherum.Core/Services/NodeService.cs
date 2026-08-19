using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>The tree rules: positions, moves, privacy, tags, and links. Bodies —
/// bytes, versions, text — belong to FileService.</summary>
public class NodeService(GatherumDbContext db, INodeAuthorizer authorizer, TimeProvider clock)
{
    /// <summary>Creates the tree half of a node; FileService attaches the body before
    /// saving. Position and inherited privacy are decided here so every node obeys the
    /// same tree rules.</summary>
    public async Task<Node> CreateNodeAsync(Guid userId, Guid? parentId, string title,
        string mediaType, CancellationToken ct = default)
    {
        Node? parent = null;
        if (parentId is { } id)
            parent = await GetVisibleAsync(userId, id, ct);

        var now = clock.GetUtcNow();
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Title = title,
            MediaType = mediaType,
            ParentId = parent?.Id,
            Position = await db.Nodes.CountAsync(n => n.ParentId == parentId, ct),
            OwnerId = userId,
            PrivateToUserId = parent?.PrivateToUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Nodes.Add(node);
        return node;
    }

    public async Task<Node> GetVisibleAsync(Guid userId, Guid nodeId, CancellationToken ct = default)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || !authorizer.CanSee(node, userId))
            throw new NotFoundException($"Node {nodeId} not found.");
        return node;
    }

    public async Task<Node> GetWithBodyAsync(Guid userId, Guid nodeId, CancellationToken ct = default)
    {
        var node = await db.Nodes
            .Include(n => n.File!).ThenInclude(f => f.Versions)
            .Include(n => n.Tags).ThenInclude(t => t.Tag)
            .FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || !authorizer.CanSee(node, userId))
            throw new NotFoundException($"Node {nodeId} not found.");
        return node;
    }

    public Task<List<Node>> GetChildrenAsync(Guid userId, Guid? parentId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.ParentId == parentId)
            .OrderBy(n => n.Position)
            .ToListAsync(ct);

    /// <summary>The whole visible tree as a flat, ordered list; callers nest it.</summary>
    public Task<List<TreeNode>> GetTreeAsync(Guid userId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .OrderBy(n => n.ParentId).ThenBy(n => n.Position)
            .Select(n => new TreeNode(n.Id, n.ParentId, n.Title, n.MediaType, n.Position,
                n.PrivateToUserId != null))
            .ToListAsync(ct);

    public async Task RenameAsync(Guid userId, Guid nodeId, string title, CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        node.Title = title;
        node.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveAsync(Guid userId, Guid nodeId, Guid? newParentId, int? position = null,
        CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        if (newParentId is { } parentId)
        {
            var parent = await GetVisibleAsync(userId, parentId, ct);
            if (parent.Id == node.Id || await IsDescendantAsync(parent.Id, node.Id, ct))
                throw new ForbiddenException("Cannot move a node into its own subtree.");
        }

        var oldSiblings = await SiblingsAsync(node.ParentId, ct);
        oldSiblings.Remove(node);
        Renumber(oldSiblings);

        var newSiblings = node.ParentId == newParentId
            ? oldSiblings
            : await SiblingsAsync(newParentId, ct);
        var index = Math.Clamp(position ?? newSiblings.Count, 0, newSiblings.Count);
        newSiblings.Insert(index, node);
        Renumber(newSiblings);

        node.ParentId = newParentId;
        node.UpdatedAt = clock.GetUtcNow();
        await RecomputePrivacyAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid userId, Guid nodeId, CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        db.Nodes.Remove(node);
        var siblings = await SiblingsAsync(node.ParentId, ct);
        siblings.Remove(node);
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPrivateAsync(Guid userId, Guid nodeId, bool isPrivate,
        CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        if (node.OwnerId != userId)
            throw new ForbiddenException("Only the owner can change a node's privacy.");
        node.IsPrivate = isPrivate;
        await RecomputePrivacyAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddTagAsync(Guid userId, Guid nodeId, string tagName, CancellationToken ct = default)
    {
        var name = NormalizeTag(tagName);
        if (name.Length == 0)
            return;
        var node = await GetWithBodyAsync(userId, nodeId, ct);
        if (node.Tags.Any(t => t.Tag!.Name == name))
            return;
        var tag = await db.Tags.FirstOrDefaultAsync(t => t.Name == name, ct)
            ?? db.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = name }).Entity;
        node.Tags.Add(new NodeTag { NodeId = node.Id, TagId = tag.Id, Tag = tag });
        RefreshSearchText(node);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveTagAsync(Guid userId, Guid nodeId, string tagName, CancellationToken ct = default)
    {
        var name = NormalizeTag(tagName);
        var node = await GetWithBodyAsync(userId, nodeId, ct);
        var nodeTag = node.Tags.FirstOrDefault(t => t.Tag!.Name == name);
        if (nodeTag is null)
            return;
        node.Tags.Remove(nodeTag);
        db.NodeTags.Remove(nodeTag);
        RefreshSearchText(node);
        await db.SaveChangesAsync(ct);
    }

    public Task<List<TagSummary>> ListTagsAsync(Guid userId, string? prefix = null,
        CancellationToken ct = default)
    {
        var normalized = prefix is null ? null : NormalizeTag(prefix);
        return db.Tags
            .Where(t => normalized == null || t.Name.StartsWith(normalized))
            .Where(t => t.Nodes.Any(nt =>
                nt.Node!.PrivateToUserId == null || nt.Node.PrivateToUserId == userId))
            .OrderBy(t => t.Name)
            .Select(t => new TagSummary(t.Name,
                t.Nodes.Count(nt => nt.Node!.PrivateToUserId == null || nt.Node.PrivateToUserId == userId)))
            .ToListAsync(ct);
    }

    public Task<List<Node>> GetNodesWithTagAsync(Guid userId, string tagName, CancellationToken ct = default)
    {
        var name = NormalizeTag(tagName);
        return authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.Tags.Any(t => t.Tag!.Name == name))
            .OrderBy(n => n.Title)
            .ToListAsync(ct);
    }

    /// <summary>Which of these titles name a node the user can see — what a
    /// <c>[[wiki link]]</c> needs, since it addresses a page by name rather than by id.
    /// Matching ignores case, because that is how people type a title they remember.
    /// Titles are not unique, so a tie goes to the exact-case match and then to the
    /// oldest node: the same name resolves to the same node for everyone, every time.</summary>
    public async Task<IReadOnlyDictionary<string, Guid>> ResolveTitlesAsync(Guid userId,
        IReadOnlyCollection<string> titles, CancellationToken ct = default)
    {
        var resolved = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var wanted = titles
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return resolved;

        var lowered = wanted.Select(t => t.ToLowerInvariant()).ToList();
        var candidates = await authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => lowered.Contains(n.Title.ToLower()))
            .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id)
            .Select(n => new { n.Id, n.Title })
            .ToListAsync(ct);

        foreach (var title in wanted)
        {
            var matches = candidates
                .Where(c => string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var match = matches.FirstOrDefault(c => c.Title == title) ?? matches.FirstOrDefault();
            if (match is not null)
                resolved[title] = match.Id;
        }
        return resolved;
    }

    public Task<List<Node>> GetBacklinksAsync(Guid userId, Guid nodeId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.OutboundLinks.Any(l => l.TargetId == nodeId))
            .OrderBy(n => n.Title)
            .ToListAsync(ct);

    /// <summary>Nodes related to this one: each shared tag scores one, a body link in
    /// either direction scores two (a deliberate mention is a stronger signal than a
    /// shared label). Ties go to the most recently updated.</summary>
    public async Task<List<SimilarNode>> GetSimilarAsync(Guid userId, Guid nodeId, int limit = 5,
        CancellationToken ct = default)
    {
        // Resolve visibility first so a private node's tags and links never leak
        // into scores computed for the other user.
        await GetVisibleAsync(userId, nodeId, ct);
        limit = Math.Clamp(limit, 1, 20);

        var tagIds = await db.NodeTags
            .Where(t => t.NodeId == nodeId).Select(t => t.TagId).ToListAsync(ct);
        var linkedIds = await db.NodeLinks
            .Where(l => l.SourceId == nodeId || l.TargetId == nodeId)
            .Select(l => l.SourceId == nodeId ? l.TargetId : l.SourceId)
            .ToListAsync(ct);

        var candidates = await authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.Id != nodeId)
            .Where(n => linkedIds.Contains(n.Id) || n.Tags.Any(t => tagIds.Contains(t.TagId)))
            .Select(n => new
            {
                n.Id, n.Title, n.MediaType, n.UpdatedAt,
                SharedTags = n.Tags.Count(t => tagIds.Contains(t.TagId)),
                IsLinked = linkedIds.Contains(n.Id),
            })
            .ToListAsync(ct);

        return candidates
            .OrderByDescending(c => c.SharedTags + (c.IsLinked ? 2 : 0))
            .ThenByDescending(c => c.UpdatedAt)
            .Take(limit)
            .Select(c => new SimilarNode(c.Id, c.Title,
                c.MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File))
            .ToList();
    }

    /// <summary>Search text is tags + filename + description + extracted text, plus
    /// whatever a model read or heard in the file; the title contributes through its
    /// own tsvector weight. A transcript and a summary are indexed side by side on
    /// purpose: the transcript answers an exact phrase someone remembers seeing or
    /// hearing, the summary answers the subject nobody said out loud.</summary>
    public void RefreshSearchText(Node node)
    {
        var tags = string.Join(' ', node.Tags.Select(t => t.Tag!.Name));
        if (node.File is not { Versions.Count: > 0 } file)
        {
            node.SearchText = tags;
            return;
        }
        var current = file.Current;
        node.SearchText = string.Join('\n', tags, current.FileName, file.Description,
            current.ExtractedText, current.Transcript, current.Summary);
    }

    public async Task ReplaceLinksAsync(Node node, IReadOnlySet<Guid> targetIds, CancellationToken ct)
    {
        var existing = await db.NodeLinks.Where(l => l.SourceId == node.Id).ToListAsync(ct);
        db.NodeLinks.RemoveRange(existing.Where(l => !targetIds.Contains(l.TargetId)));
        var current = existing.Select(l => l.TargetId).ToHashSet();
        var valid = await db.Nodes
            .Where(n => targetIds.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct);
        db.NodeLinks.AddRange(valid.Where(id => !current.Contains(id) && id != node.Id)
            .Select(id => new NodeLink { SourceId = node.Id, TargetId = id }));
    }

    private async Task<bool> IsDescendantAsync(Guid candidate, Guid ancestor, CancellationToken ct)
    {
        var parents = await db.Nodes.Select(n => new { n.Id, n.ParentId }).ToListAsync(ct);
        var byId = parents.ToDictionary(n => n.Id, n => n.ParentId);
        for (var current = byId.GetValueOrDefault(candidate); current is { } id;
            current = byId.GetValueOrDefault(id))
        {
            if (id == ancestor)
                return true;
        }
        return false;
    }

    private Task<List<Node>> SiblingsAsync(Guid? parentId, CancellationToken ct) =>
        db.Nodes.Where(n => n.ParentId == parentId).OrderBy(n => n.Position).ToListAsync(ct);

    private static void Renumber(List<Node> siblings)
    {
        for (var i = 0; i < siblings.Count; i++)
            siblings[i].Position = i;
    }

    /// <summary>Recomputes the denormalized privacy owner for every node. The whole tree
    /// fits in memory for a two-person knowledge base, so a full pass beats a recursive
    /// SQL walk in both clarity and correctness.</summary>
    private async Task RecomputePrivacyAsync(CancellationToken ct)
    {
        var nodes = await db.Nodes.ToListAsync(ct);
        var children = nodes.ToLookup(n => n.ParentId);
        var pending = new Stack<(Node Node, Guid? Inherited)>(
            children[null].Select(n => (n, (Guid?)null)));
        while (pending.Count > 0)
        {
            var (node, inherited) = pending.Pop();
            node.PrivateToUserId = node.IsPrivate ? node.OwnerId : inherited;
            foreach (var child in children[node.Id])
                pending.Push((child, node.PrivateToUserId));
        }
    }

    private static string NormalizeTag(string tag) => tag.Trim().ToLowerInvariant();
}

public record TreeNode(Guid Id, Guid? ParentId, string Title, string MediaType, int Position,
    bool IsPrivate)
{
    public NodeKind Kind => MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File;
}

public record TagSummary(string Name, int NodeCount);

public record SimilarNode(Guid Id, string Title, NodeKind Kind);
