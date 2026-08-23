using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>Owns everything derived about who may reach a node: the effective-public flag
/// and the <see cref="NodeAccessEntry"/> closure. Access is additive downward — sharing a
/// directory shares what is in it — so the closure is one pre-order walk, the same shape
/// the private-subtree flag used before it.
///
/// Authority runs one way between ownership and access: only an owner may declare
/// anything about a node, and because the node tree is the directory tree, every ancestor
/// inside a root shares that root's owner. Ownership decides who gets to say; it does not
/// narrow what may be said.</summary>
public class AccessService(GatherumDbContext db, TimeProvider clock, NodeMetadataWriter sidecar)
{
    public async Task SetAccessAsync(Guid userId, Guid nodeId, AccessMode mode, bool inherit = true,
        CancellationToken ct = default)
    {
        var node = await RequireOwnedAsync(userId, nodeId, ct);
        node.Access = mode;
        node.InheritAccess = inherit;
        node.UpdatedAt = clock.GetUtcNow();
        if (mode == AccessMode.Private)
            db.NodeGrants.RemoveRange(await db.NodeGrants.Where(g => g.NodeId == nodeId).ToListAsync(ct));
        await ApplyAsync(nodeId, ct);
    }

    public async Task GrantAsync(Guid userId, Guid nodeId, Guid granteeId, AccessRole role,
        CancellationToken ct = default)
    {
        var node = await RequireOwnedAsync(userId, nodeId, ct);
        if (granteeId == node.OwnerId)
            throw new ForbiddenException("The owner already has full access.");
        if (!await db.Users.AnyAsync(u => u.Id == granteeId, ct))
            throw new NotFoundException($"User {granteeId} not found.");

        var grant = await db.NodeGrants.FirstOrDefaultAsync(g => g.NodeId == nodeId && g.UserId == granteeId, ct);
        if (grant is null)
            db.NodeGrants.Add(new NodeGrant { NodeId = nodeId, UserId = granteeId, Role = role });
        else
            grant.Role = role;

        // Naming somebody is the gesture; the mode follows it rather than being a second
        // thing to remember. Publishing is never implied by sharing with a person.
        if (node.Access == AccessMode.Private)
            node.Access = AccessMode.Shared;
        node.UpdatedAt = clock.GetUtcNow();
        await ApplyAsync(nodeId, ct);
    }

    public async Task RevokeAsync(Guid userId, Guid nodeId, Guid granteeId, CancellationToken ct = default)
    {
        var node = await RequireOwnedAsync(userId, nodeId, ct);
        var grant = await db.NodeGrants.FirstOrDefaultAsync(g => g.NodeId == nodeId && g.UserId == granteeId, ct);
        if (grant is not null)
            db.NodeGrants.Remove(grant);
        if (node.Access == AccessMode.Shared
            && !await db.NodeGrants.AnyAsync(g => g.NodeId == nodeId && g.UserId != granteeId, ct))
            node.Access = AccessMode.Private;
        node.UpdatedAt = clock.GetUtcNow();
        await ApplyAsync(nodeId, ct);
    }

    /// <summary>Persist the declaration, then rebuild the closure from it, then persist
    /// that. The order is the whole point: <see cref="RecomputeAsync"/> reads the grants
    /// back out of the database, and a grant that has only been added to the change
    /// tracker is not something a query can see.</summary>
    private async Task ApplyAsync(Guid nodeId, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await RecomputeAsync(ct);
        await db.SaveChangesAsync(ct);
        // An access rule that lived only in the database would be the one thing a rebuild
        // could not recover — and it would come back wrong in the unsafe direction.
        await sidecar.WriteAsync(nodeId, ct);
    }

    /// <summary>Rebuilds the closure for the whole tree. Called after anything that can
    /// move access around — a declaration, a grant, a move, a reindex. Whole-tree because
    /// Gatherum is a knowledge base for two people and a correct answer beats a clever
    /// one; the walk is the same one the private-subtree flag always did.</summary>
    public async Task RecomputeAsync(CancellationToken ct = default)
    {
        var nodes = await db.Nodes.ToListAsync(ct);
        var grants = (await db.NodeGrants.ToListAsync(ct)).ToLookup(g => g.NodeId);
        var existing = await db.NodeAccessEntries.ToListAsync(ct);
        var children = nodes.ToLookup(n => n.ParentId);

        var wanted = new Dictionary<(Guid Node, Guid User), AccessRole>();
        var pending = new Stack<(Node Node, NodeReach Inherited, Dictionary<Guid, AccessRole> Grants)>(
            children[null].Select(n => (n, NodeReach.None, new Dictionary<Guid, AccessRole>())));

        while (pending.Count > 0)
        {
            var (node, inheritedReach, inherited) = pending.Pop();
            var carried = node.InheritAccess ? inherited : [];

            // Reach is additive downward like the grants are, so it is a maximum: a page
            // inside a published directory is published, and inherit:false is how a
            // subtree stays tighter than what contains it.
            node.Reach = node.InheritAccess
                ? (NodeReach)Math.Max((int)node.Access.Reach(), (int)inheritedReach)
                : node.Access.Reach();

            var effective = new Dictionary<Guid, AccessRole>(carried);
            foreach (var grant in grants[node.Id])
            {
                // The stronger role wins wherever a name appears twice: an editor of a
                // directory is not demoted by being named a reader further down.
                if (!effective.TryGetValue(grant.UserId, out var held) || grant.Role > held)
                    effective[grant.UserId] = grant.Role;
            }
            // An owner is never a grantee of their own node; the authorizer knows them.
            effective.Remove(node.OwnerId);

            foreach (var (user, role) in effective)
                wanted[(node.Id, user)] = role;

            foreach (var child in children[node.Id])
                pending.Push((child, node.Reach, effective));
        }

        foreach (var entry in existing)
        {
            if (wanted.TryGetValue((entry.NodeId, entry.UserId), out var role))
            {
                entry.Role = role;
                wanted.Remove((entry.NodeId, entry.UserId));
            }
            else
            {
                db.NodeAccessEntries.Remove(entry);
            }
        }
        db.NodeAccessEntries.AddRange(wanted.Select(w => new NodeAccessEntry
        {
            NodeId = w.Key.Node,
            UserId = w.Key.User,
            Role = w.Value,
        }));
    }

    private async Task<Node> RequireOwnedAsync(Guid userId, Guid nodeId, CancellationToken ct)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, ct)
            ?? throw new NotFoundException($"Node {nodeId} not found.");
        if (node.OwnerId != userId)
            throw new ForbiddenException("Only the owner can change who may reach a node.");
        return node;
    }
}
