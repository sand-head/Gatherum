using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

public class NodeService(GatherumDbContext db, INodeAuthorizer authorizer, TimeProvider clock)
{
    /// <summary>Rapid autosaves by the same author fold into the latest revision instead
    /// of flooding history with keystroke-sized snapshots.</summary>
    private static readonly TimeSpan RevisionCollapseWindow = TimeSpan.FromMinutes(5);

    /// <summary>Two live editors autosave the same page concurrently; serializing saves
    /// per node keeps revision numbers and link rows race-free. Process-wide is enough:
    /// Gatherum deploys as a single instance.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim>
        SaveGates = new();

    public async Task<Node> CreatePageAsync(Guid userId, Guid? parentId, string title,
        string? docJson = null, CancellationToken ct = default)
    {
        var node = await CreateNodeAsync(userId, parentId, title, NodeKind.Page, ct);
        node.Page = new PageBody { NodeId = node.Id, Doc = docJson ?? PageMarkdown.EmptyDoc };
        await AddRevisionAsync(node, userId, ct);
        RefreshPageDerivedState(node);
        await db.SaveChangesAsync(ct);
        return node;
    }

    /// <summary>Creates the tree half of a node; FileService attaches the file body
    /// before saving. Position and inherited privacy are decided here so every kind
    /// of node obeys the same tree rules.</summary>
    public async Task<Node> CreateNodeAsync(Guid userId, Guid? parentId, string title,
        NodeKind kind, CancellationToken ct = default)
    {
        Node? parent = null;
        if (parentId is { } id)
            parent = await GetVisibleAsync(userId, id, ct);

        var now = clock.GetUtcNow();
        var node = new Node
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Title = title,
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
            .Include(n => n.Page)
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
            .Select(n => new TreeNode(n.Id, n.ParentId, n.Title, n.Kind, n.Position,
                n.PrivateToUserId != null))
            .ToListAsync(ct);

    /// <summary>Saves a page body. Edits that arrive from outside a live editing session
    /// (REST, MCP, revision restore) set <paramref name="resetCollabState"/> so the next
    /// editor to open the page re-seeds its collaboration doc from this content instead
    /// of resurrecting the stale CRDT state.</summary>
    public async Task<Node> SavePageAsync(Guid userId, Guid nodeId, string docJson,
        string? title = null, bool resetCollabState = false, CancellationToken ct = default)
    {
        var gate = SaveGates.GetOrAdd(nodeId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var node = await GetWithBodyAsync(userId, nodeId, ct);
            if (node.Page is null)
                throw new NotFoundException($"Node {nodeId} is not a page.");

            if (resetCollabState)
                await db.YjsDocs.Where(d => d.NodeId == nodeId).ExecuteDeleteAsync(ct);
            node.Page.Doc = docJson;
            if (title is not null)
                node.Title = title;
            node.UpdatedAt = clock.GetUtcNow();
            await AddRevisionAsync(node, userId, ct);
            RefreshPageDerivedState(node);
            await ReplaceLinksAsync(node, PageMarkdown.LinkedNodeIds(docJson), ct);
            await db.SaveChangesAsync(ct);
            return node;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RenameAsync(Guid userId, Guid nodeId, string title, CancellationToken ct = default)
    {
        var node = await GetWithBodyAsync(userId, nodeId, ct);
        node.Title = title;
        node.UpdatedAt = clock.GetUtcNow();
        if (node.Page is not null)
            await AddRevisionAsync(node, userId, ct);
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

    public Task<List<Node>> GetBacklinksAsync(Guid userId, Guid nodeId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.OutboundLinks.Any(l => l.TargetId == nodeId))
            .OrderBy(n => n.Title)
            .ToListAsync(ct);

    public Task<List<Revision>> GetRevisionsAsync(Guid userId, Guid nodeId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.Id == nodeId)
            .SelectMany(n => n.Revisions)
            .OrderByDescending(r => r.Number)
            .ToListAsync(ct);

    public async Task<Node> RestoreRevisionAsync(Guid userId, Guid nodeId, int revisionNumber,
        CancellationToken ct = default)
    {
        var revision = await db.Revisions
            .FirstOrDefaultAsync(r => r.NodeId == nodeId && r.Number == revisionNumber, ct)
            ?? throw new NotFoundException($"Revision {revisionNumber} of node {nodeId} not found.");
        return await SavePageAsync(userId, nodeId, revision.Doc, revision.Title,
            resetCollabState: true, ct: ct);
    }

    /// <summary>Refreshes search text and, for files, must be called after versions or
    /// description change. Tags always contribute so tag words are searchable.</summary>
    public void RefreshSearchText(Node node)
    {
        var tags = string.Join(' ', node.Tags.Select(t => t.Tag!.Name));
        node.SearchText = node switch
        {
            { Page: { } page } => $"{tags}\n{PageMarkdown.ToPlainText(page.Doc)}",
            { File: { } file } when file.Versions.Count > 0 =>
                $"{tags}\n{file.Current.FileName}\n{file.Description}\n{file.Current.ExtractedText}",
            { File: { } file } => $"{tags}\n{file.Description}",
            _ => tags,
        };
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

    private void RefreshPageDerivedState(Node node) => RefreshSearchText(node);

    private async Task AddRevisionAsync(Node node, Guid authorId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var latest = await db.Revisions
            .Where(r => r.NodeId == node.Id)
            .OrderByDescending(r => r.Number)
            .FirstOrDefaultAsync(ct);
        if (latest is not null && latest.AuthorId == authorId &&
            now - latest.CreatedAt < RevisionCollapseWindow)
        {
            latest.Title = node.Title;
            latest.Doc = node.Page!.Doc;
            return;
        }
        db.Revisions.Add(new Revision
        {
            Id = Guid.NewGuid(),
            NodeId = node.Id,
            Number = (latest?.Number ?? 0) + 1,
            Title = node.Title,
            Doc = node.Page!.Doc,
            AuthorId = authorId,
            CreatedAt = now,
        });
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

public record TreeNode(Guid Id, Guid? ParentId, string Title, NodeKind Kind, int Position, bool IsPrivate);

public record TagSummary(string Name, int NodeCount);
