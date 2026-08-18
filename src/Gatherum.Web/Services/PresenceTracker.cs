using System.Collections.Concurrent;

namespace Gatherum.Web.Services;

/// <summary>Who is editing which node right now. Heartbeat-based: editors report in
/// every few seconds and entries expire shortly after they stop, so a closed tab never
/// leaves a ghost. In-process state is enough for a single-instance deployment.</summary>
public sealed class PresenceTracker(TimeProvider clock)
{
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, Editor>> byNode = new();

    private sealed record Editor(string Name, DateTimeOffset LastSeen);

    public void Heartbeat(Guid nodeId, Guid userId, string userName)
    {
        var editors = byNode.GetOrAdd(nodeId, _ => new ConcurrentDictionary<Guid, Editor>());
        editors[userId] = new Editor(userName, clock.GetUtcNow());
    }

    public void Leave(Guid nodeId, Guid userId)
    {
        if (byNode.TryGetValue(nodeId, out var editors))
            editors.TryRemove(userId, out _);
    }

    public IReadOnlyList<string> OthersEditing(Guid nodeId, Guid exceptUserId)
    {
        if (!byNode.TryGetValue(nodeId, out var editors))
            return [];
        var cutoff = clock.GetUtcNow() - Expiry;
        foreach (var (userId, editor) in editors)
        {
            if (editor.LastSeen < cutoff)
                editors.TryRemove(userId, out _);
        }
        return editors
            .Where(e => e.Key != exceptUserId)
            .Select(e => e.Value.Name)
            .Order()
            .ToList();
    }
}
