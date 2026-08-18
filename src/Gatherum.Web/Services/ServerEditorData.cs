using Gatherum.Client;
using Gatherum.Core.Markdown;
using Gatherum.Web.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace Gatherum.Web.Services;

/// <summary>The editor's data source while its Interactive Auto component renders on
/// the server: same contract as the WASM HTTP implementation, but straight into the
/// application services under the circuit's authenticated user.</summary>
public sealed class ServerEditorData(
    AppOperations ops,
    PresenceTracker presence,
    AuthenticationStateProvider authentication) : IEditorData
{
    public async Task<EditorPayload> LoadAsync(Guid nodeId)
    {
        var userId = await UserIdAsync();
        var head = await ops.Files(s => s.GetHeadVersionAsync(userId, nodeId));
        var text = await ops.Files(s => s.GetTextAsync(userId, nodeId));
        return new EditorPayload(text, head);
    }

    public async Task<int> SaveTextAsync(Guid nodeId, string text)
    {
        var userId = await UserIdAsync();
        var version = await ops.Files(s => s.SaveTextAsync(userId, nodeId, text));
        return version.Number;
    }

    public async Task<PresenceInfo> HeartbeatAsync(Guid nodeId)
    {
        var state = await authentication.GetAuthenticationStateAsync();
        var userId = state.User.GetUserId();
        presence.Heartbeat(nodeId, userId, state.User.Identity?.Name ?? "someone");
        var head = await ops.Files(s => s.GetHeadVersionAsync(userId, nodeId));
        return new PresenceInfo(presence.OthersEditing(nodeId, userId), head);
    }

    public async Task LeaveAsync(Guid nodeId) =>
        presence.Leave(nodeId, await UserIdAsync());

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query)
    {
        var userId = await UserIdAsync();
        var results = await ops.Search(s => s.SearchAsync(userId, query, limit: 8));
        return results
            .Select(r => new SearchHit(r.Id, r.Kind.ToString(), r.Title, r.Snippet))
            .ToList();
    }

    public async Task<byte[]?> GetImageAsync(string url)
    {
        if (MarkdownContent.NodeIdFromUrl(url) is not { } imageNodeId)
            return null;
        var userId = await UserIdAsync();
        try
        {
            var content = await ops.Files(s => s.OpenContentAsync(userId, imageNodeId));
            await using (content.Stream)
            {
                using var buffer = new MemoryStream();
                await content.Stream.CopyToAsync(buffer);
                return buffer.ToArray();
            }
        }
        catch (Gatherum.Core.NotFoundException)
        {
            return null;
        }
    }

    private async Task<Guid> UserIdAsync() =>
        (await authentication.GetAuthenticationStateAsync()).User.GetUserId();
}
