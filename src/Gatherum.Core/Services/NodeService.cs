using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>The tree rules: positions, moves, privacy, and links. Bodies — bytes,
/// versions, text — belong to FileService; the taxonomy to CategoryService.</summary>
public class NodeService(GatherumDbContext db, INodeAuthorizer authorizer, TimeProvider clock,
    EmbeddingService embeddings, AccessService access, NodeMetadataWriter sidecar)
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
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Nodes.Add(node);
        return node;
    }

    public async Task<Node> GetVisibleAsync(Guid? userId, Guid nodeId, CancellationToken ct = default)
    {
        var node = await db.Nodes.Include(n => n.AccessEntries)
            .FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || !authorizer.CanSee(node, userId))
            throw new NotFoundException($"Node {nodeId} not found.");
        return node;
    }

    public async Task<Node> GetWithBodyAsync(Guid? userId, Guid nodeId, CancellationToken ct = default)
    {
        var node = await db.Nodes
            .Include(n => n.AccessEntries)
            .Include(n => n.File!).ThenInclude(f => f.Versions)
            .Include(n => n.Categories).ThenInclude(c => c.Category)
            .FirstOrDefaultAsync(n => n.Id == nodeId, ct);
        if (node is null || !authorizer.CanSee(node, userId))
            throw new NotFoundException($"Node {nodeId} not found.");
        return node;
    }

    public Task<List<Node>> GetChildrenAsync(Guid? userId, Guid? parentId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.ParentId == parentId)
            .OrderBy(n => n.Position)
            .ToListAsync(ct);

    /// <summary>The whole visible tree as a flat, ordered list; callers nest it. Because
    /// ownership is the path and access is not, this is a union rather than a listing:
    /// what the caller owns, plus what has been shared with them from somebody else's
    /// root. <see cref="TreeNode.Owned"/> is how the UI tells the two apart.</summary>
    public Task<List<TreeNode>> GetTreeAsync(Guid? userId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .OrderBy(n => n.ParentId).ThenBy(n => n.Position)
            .Select(n => new TreeNode(n.Id, n.ParentId, n.Title, n.MediaType, n.Position,
                n.Access, n.Reach, n.ListedToSignedIn, userId != null && n.OwnerId == userId))
            .ToListAsync(ct);

    /// <summary>Content writes: the owner, or somebody granted Editor. Seeing a node has
    /// never been the same as being allowed to change it, and Reader means Reader.</summary>
    public void EnsureEditable(Node node, Guid? userId)
    {
        if (!authorizer.CanEdit(node, userId))
            throw new ForbiddenException($"Node {node.Id} is not yours to change.");
    }

    /// <summary>Structural changes — renaming, moving, deleting — are the owner's alone.
    /// Ownership is the path, so these move files around inside somebody's directory, and
    /// an editor was given a document rather than a filing cabinet.</summary>
    public static void EnsureOwner(Node node, Guid? userId)
    {
        if (userId is not { } id || node.OwnerId != id)
            throw new ForbiddenException($"Only the owner can restructure node {node.Id}.");
    }

    public async Task RenameAsync(Guid? userId, Guid nodeId, string title, CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        EnsureOwner(node, userId);
        node.Title = title;
        node.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        // A title that no longer matches the filename is an override, and an override
        // only the index knows about is one a reindex would quietly undo.
        await sidecar.WriteAsync(node.Id, ct);
        if (!node.IsCategory)
            return;
        // A category's name is written into the search text of everything under it, and
        // into the sidecar of everything filed directly in it — which is the whole price
        // of renaming one now. Nothing is repathed, because there is no path.
        await RefreshCategoryReachAsync(node, ct);
        foreach (var memberId in await db.NodeCategories.Where(m => m.CategoryId == node.Id)
            .Select(m => m.NodeId).ToListAsync(ct))
            await sidecar.WriteAsync(memberId, ct);
    }

    public async Task MoveAsync(Guid? userId, Guid nodeId, Guid? newParentId, int? position = null,
        CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        EnsureOwner(node, userId);
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
        await access.RecomputeAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deleting a category page unfiles everything that was in it and leaves the
    /// pages alone — which is what deleting a category has always meant here. What is new
    /// is that its subcategories are pages too, so they are not deleted with it: they lose
    /// a parent and become subjects of their own.</summary>
    public async Task DeleteAsync(Guid? userId, Guid nodeId, CancellationToken ct = default)
    {
        var node = await GetVisibleAsync(userId, nodeId, ct);
        EnsureOwner(node, userId);
        // The category's name is in the search text of everything it reaches, so note who
        // that is before the cascade takes the memberships away with it.
        var orphaned = node.IsCategory ? await CategoryReachAsync(node.Id, ct) : [];
        db.Nodes.Remove(node);
        var siblings = await SiblingsAsync(node.ParentId, ct);
        siblings.Remove(node);
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
        orphaned.Remove(node.Id);
        await RefreshSearchTextAsync(orphaned, await CategoryIndex.LoadAsync(db, ct), ct);
    }

    /// <summary>Which of these titles name a node the user can see — what a
    /// <c>[[wiki link]]</c> needs, since it addresses a page by name rather than by id.
    /// Matching ignores case, because that is how people type a title they remember.
    /// Titles are not unique, so a tie goes to the exact-case match and then to the
    /// oldest node: the same name resolves to the same node for everyone, every time.</summary>
    public async Task<IReadOnlyDictionary<string, Guid>> ResolveTitlesAsync(Guid? userId,
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

    /// <summary>How many links one page may ask about at a time.</summary>
    public const int MaxLinkTargets = 500;

    /// <summary>Which of these ids the caller may actually reach. A page is free to link
    /// a node its reader is not allowed to open — a public page linking its author's
    /// private file is the ordinary case, not the exotic one — so a rendered page asks
    /// this about every node it links and dresses the ones that come back missing as
    /// locked rather than as links that go nowhere.
    ///
    /// <see cref="INodeAuthorizer.CanSee"/>, not <see cref="INodeAuthorizer.VisibleTo"/>:
    /// a link is the direct-reach question, and an unlisted node is reachable by the link
    /// that names it. Bounded because the caller may be the internet — a page with more
    /// links than <see cref="MaxLinkTargets"/> gets the first of them answered, and the
    /// rest are treated as unreachable, which errs toward the lock.</summary>
    public async Task<IReadOnlySet<Guid>> ReachableIdsAsync(Guid? userId,
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().Take(MaxLinkTargets).ToList();
        if (wanted.Count == 0)
            return new HashSet<Guid>();

        var candidates = await db.Nodes.Include(n => n.AccessEntries)
            .Where(n => wanted.Contains(n.Id))
            .ToListAsync(ct);
        return candidates.Where(n => authorizer.CanSee(n, userId)).Select(n => n.Id).ToHashSet();
    }

    public Task<List<Node>> GetBacklinksAsync(Guid? userId, Guid nodeId, CancellationToken ct = default) =>
        authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.OutboundLinks.Any(l => l.TargetId == nodeId))
            .OrderBy(n => n.Title)
            .ToListAsync(ct);

    /// <summary>Nodes related to this one. A body link in either direction scores four
    /// — a deliberate mention is the strongest signal there is; a category both nodes
    /// sit in scores two; a category one shares with the other's ancestry scores one,
    /// because "somewhere under Homelab" is a weaker kinship than "both in Podman".
    /// Ties go to the most recently updated. A category page is never its own
    /// suggestion: it is what the pages have in common, not another page like them.</summary>
    public async Task<List<SimilarNode>> GetSimilarAsync(Guid? userId, Guid nodeId, int limit = 5,
        CancellationToken ct = default)
    {
        // Resolve visibility first so a private node's categories and links never leak
        // into scores computed for the other user.
        await GetVisibleAsync(userId, nodeId, ct);
        limit = Math.Clamp(limit, 1, 20);

        var subjects = await db.NodeCategories
            .Where(c => c.NodeId == nodeId).Select(c => c.CategoryId).ToListAsync(ct);
        var linkedIds = await db.NodeLinks
            .Where(l => l.SourceId == nodeId || l.TargetId == nodeId)
            .Select(l => l.SourceId == nodeId ? l.TargetId : l.SourceId)
            .ToListAsync(ct);

        // Which categories touch this node's ancestry is decided over the whole (small)
        // taxonomy in memory, so the node query stays one Contains rather than a walk
        // per ancestor.
        var taxonomy = await CategoryIndex.LoadAsync(db, ct);
        var direct = subjects.ToHashSet();
        var ancestry = taxonomy.ClosureOf(subjects);
        var kin = taxonomy.Ids
            .Where(id => taxonomy.ClosureOf([id]).Any(ancestry.Contains))
            .ToList();

        // What the taxonomy and the links cannot say: two pages about the same thing
        // that were never filed together and never mentioned each other.
        var likeness = await LikenessAsync(userId, nodeId, limit, ct);
        var alike = likeness.Keys.ToList();

        var candidates = await authorizer.VisibleTo(db.Nodes, userId)
            .Where(n => n.Id != nodeId && !n.IsCategory)
            .Where(n => linkedIds.Contains(n.Id)
                || alike.Contains(n.Id)
                || n.Categories.Any(c => kin.Contains(c.CategoryId)))
            .Select(n => new
            {
                n.Id, n.Title, n.MediaType, n.UpdatedAt,
                Subjects = n.Categories.Select(c => c.CategoryId).ToList(),
                IsLinked = linkedIds.Contains(n.Id),
            })
            .ToListAsync(ct);

        return candidates
            .OrderByDescending(c => Kinship(c.Subjects) + (c.IsLinked ? 4 : 0)
                + likeness.GetValueOrDefault(c.Id) * 4)
            .ThenByDescending(c => c.UpdatedAt)
            .Take(limit)
            .Select(c => new SimilarNode(c.Id, c.Title,
                c.MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File))
            .ToList();

        // A category the two nodes are both in is counted twice — once as itself and
        // once as the deepest thing their ancestries have in common — which is exactly
        // the two-to-one this method promises.
        int Kinship(List<Guid> categoryIds)
        {
            var shared = categoryIds.Count(direct.Contains);
            var common = taxonomy.ClosureOf(categoryIds).Count(ancestry.Contains);
            return shared + common;
        }
    }

    /// <summary>How alike this node's text is to other nodes', in 0..1. Weighted to be
    /// worth about what a link is worth at its strongest: writing about the same subject
    /// is real evidence of kinship, but somebody deliberately linking two pages, or
    /// filing them under one subject, is a statement and this is an inference.</summary>
    private async Task<Dictionary<Guid, double>> LikenessAsync(Guid? userId, Guid nodeId, int limit,
        CancellationToken ct)
    {
        var centroid = await embeddings.CentroidAsync(nodeId, ct);
        if (centroid is null)
            return [];

        var visible = authorizer.VisibleTo(db.Nodes, userId).Where(n => n.Id != nodeId);
        var hits = await embeddings.NearestAsync(visible, centroid, limit * 6, ct);
        var best = new Dictionary<Guid, double>();
        foreach (var hit in hits)
        {
            // Cosine distance runs 0..2, but the far half is "about the opposite of this"
            // and there is no such thing here — everything past 1 is simply unrelated.
            var score = Math.Clamp(1 - hit.Distance, 0, 1);
            if (score > best.GetValueOrDefault(hit.NodeId))
                best[hit.NodeId] = score;
        }
        return best;
    }

    /// <summary>Search text is category names + filename + description + extracted
    /// text, plus whatever a model read or heard in the file; the title contributes
    /// through its own tsvector weight. A category contributes every category it is
    /// nested under as well as itself, so searching "homelab" finds what sits only in
    /// "Podman"; a transcript and a summary are indexed side by side on purpose, because
    /// the transcript answers an exact phrase someone remembers seeing or hearing and the
    /// summary answers the subject nobody said out loud.
    ///
    /// The taxonomy comes in rather than being read here, because the callers that touch
    /// many nodes at once — a rename, a re-nesting, a reindex — would otherwise reload it
    /// per node. <see cref="RefreshSearchTextAsync"/> is the one-node convenience.</summary>
    public void RefreshSearchText(Node node, CategoryIndex taxonomy)
    {
        var categories = taxonomy.Words(node.Categories.Select(c => c.CategoryId));
        if (node.File is not { Versions.Count: > 0 } file)
        {
            node.SearchText = categories;
            return;
        }
        var current = file.Current;
        node.SearchText = string.Join('\n', categories, current.FileName, file.Description,
            current.ExtractedText, current.Transcript, current.Summary);
        // Appended rather than joined in, so every node that is not a bookmark keeps
        // byte-identical search text — and its embeddings — across this feature existing.
        if (file.SourceUrl.Length > 0)
            node.SearchText += '\n' + file.SourceUrl;
    }

    public async Task RefreshSearchTextAsync(Node node, CancellationToken ct = default) =>
        RefreshSearchText(node, await CategoryIndex.LoadAsync(db, ct));

    /// <summary>Rewrites the search text of everything a change to one node's filing can
    /// reach: the node itself, and — when it is a category — every category nested under
    /// it and every node filed in any of them, because all of them are found by its name.
    /// One taxonomy snapshot serves the lot.</summary>
    public async Task RefreshCategoryReachAsync(Node node, CancellationToken ct = default)
    {
        var reach = node.IsCategory ? await CategoryReachAsync(node.Id, ct) : [];
        reach.Add(node.Id);
        await RefreshSearchTextAsync(reach, await CategoryIndex.LoadAsync(db, ct), ct);
    }

    public async Task RefreshSearchTextAsync(IReadOnlyCollection<Guid> nodeIds,
        CategoryIndex taxonomy, CancellationToken ct = default)
    {
        if (nodeIds.Count == 0)
            return;
        var wanted = nodeIds.ToList();
        var affected = await db.Nodes
            .Where(n => wanted.Contains(n.Id))
            .Include(n => n.File!).ThenInclude(f => f.Versions)
            .Include(n => n.Categories)
            .ToListAsync(ct);
        foreach (var node in affected)
            RefreshSearchText(node, taxonomy);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Every node a category's name is part of the search text of: the category,
    /// what is nested under it, and everything filed in any of them.</summary>
    private async Task<HashSet<Guid>> CategoryReachAsync(Guid categoryId, CancellationToken ct)
    {
        var taxonomy = await CategoryIndex.LoadAsync(db, ct);
        var subtree = taxonomy.SubtreeOf(categoryId);
        var wanted = subtree.ToList();
        var members = await db.NodeCategories
            .Where(m => wanted.Contains(m.CategoryId))
            .Select(m => m.NodeId)
            .Distinct()
            .ToListAsync(ct);
        subtree.UnionWith(members);
        return subtree;
    }

    /// <summary>What a node's body links, written into <c>NodeLinks</c>. The one place
    /// that decides what a link <em>is</em>: mentions in the description, mentions and
    /// in-app file URLs in the body, and the titles a <c>[[wiki link]]</c> names, resolved
    /// as whoever is answering for the node — the author on a save, the owner on a
    /// rebuild. A docx body's extracted text is its canonical Markdown rendering, so
    /// mentions made in the document editor link and backlink the way a page's do.
    ///
    /// It lives here rather than on the save path because the save path is not the only
    /// caller: <see cref="Reindexer"/> derives the same rows from the same bodies when it
    /// rebuilds the index from cold, and two spellings of "what does this body link" would
    /// be two answers.</summary>
    public async Task RefreshLinksAsync(Node node, Guid userId, CancellationToken ct = default)
    {
        var targets = new HashSet<Guid>(
            Markdown.MarkdownContent.MentionedNodeIds(node.File!.Description));
        if (node.MediaType is MediaTypes.Markdown or MediaTypes.Docx)
        {
            var body = node.File.Current.ExtractedText;
            targets.UnionWith(Markdown.MarkdownContent.LinkedNodeIds(body));
            var wikiTargets = Markdown.WikiLinkSyntax.Targets(body);
            if (wikiTargets.Count > 0)
                targets.UnionWith((await ResolveTitlesAsync(userId, wikiTargets, ct)).Values);
        }
        await ReplaceLinksAsync(node, targets, ct);
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
}

public record TreeNode(Guid Id, Guid? ParentId, string Title, string MediaType, int Position,
    AccessMode Access, NodeReach Reach, bool ListedToSignedIn, bool Owned)
{
    public NodeKind Kind => MediaType == MediaTypes.Markdown ? NodeKind.Page : NodeKind.File;
}

public record SimilarNode(Guid Id, string Title, NodeKind Kind);
