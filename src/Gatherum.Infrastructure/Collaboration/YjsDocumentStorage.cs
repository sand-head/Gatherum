using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using YDotNet.Server.Storage;

namespace Gatherum.Infrastructure.Collaboration;

/// <summary>Persists live-collaboration CRDT state per page node. Document names are
/// node ids; unparseable names are ignored rather than stored, so the collab server
/// can never grow rows the domain doesn't know about.</summary>
public class YjsDocumentStorage(IServiceScopeFactory scopes, TimeProvider clock) : IDocumentStorage
{
    public async ValueTask<byte[]?> GetDocAsync(string name, CancellationToken ct = default)
    {
        if (!Guid.TryParse(name, out var nodeId))
            return null;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatherumDbContext>();
        var doc = await db.YjsDocs.AsNoTracking().FirstOrDefaultAsync(d => d.NodeId == nodeId, ct);
        return doc?.State;
    }

    public async ValueTask StoreDocAsync(string name, byte[] doc, CancellationToken ct = default)
    {
        if (!Guid.TryParse(name, out var nodeId))
            return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatherumDbContext>();
        var existing = await db.YjsDocs.FirstOrDefaultAsync(d => d.NodeId == nodeId, ct);
        if (existing is null)
            db.YjsDocs.Add(new YjsDoc { NodeId = nodeId, State = doc, UpdatedAt = clock.GetUtcNow() });
        else
        {
            existing.State = doc;
            existing.UpdatedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
    }
}
