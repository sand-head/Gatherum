using System.Net.Http.Json;

namespace Gatherum.Client;

/// <summary>The editor's view of the server, abstracted because an Interactive Auto
/// component lives in two homes: rendered on the server circuit it gets a direct
/// service-backed implementation, rendered in WebAssembly it gets this project's
/// HTTP one. The component itself never knows which.</summary>
public interface IEditorData
{
    Task<EditorPayload> LoadAsync(Guid nodeId);
    Task<int> SaveTextAsync(Guid nodeId, string text);
    Task<PresenceInfo> HeartbeatAsync(Guid nodeId);
    Task LeaveAsync(Guid nodeId);
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query);

    /// <summary>Bytes for an image the document embeds. Only in-app content URLs
    /// (/api/files/…/content) resolve; anything else stays a placeholder.</summary>
    Task<byte[]?> GetImageAsync(string url);
}

public record EditorPayload(string Text, int HeadVersion);
public record PresenceInfo(IReadOnlyList<string> Editors, int HeadVersion);
public record SearchHit(Guid Id, string Kind, string Title, string Snippet);

public sealed class HttpEditorData(HttpClient http) : IEditorData
{
    public async Task<EditorPayload> LoadAsync(Guid nodeId)
    {
        var text = await http.GetStringAsync($"/api/files/{nodeId}/content");
        var presence = await HeartbeatAsync(nodeId);
        return new EditorPayload(text, presence.HeadVersion);
    }

    public async Task<int> SaveTextAsync(Guid nodeId, string text)
    {
        var response = await http.PutAsJsonAsync($"/api/text/{nodeId}", new { text });
        response.EnsureSuccessStatusCode();
        var saved = await response.Content.ReadFromJsonAsync<SaveResult>();
        return saved?.Version ?? 0;
    }

    public async Task<PresenceInfo> HeartbeatAsync(Guid nodeId) =>
        await http.GetFromJsonAsync<PresenceInfo>($"/api/nodes/{nodeId}/presence?editing=true")
            ?? new PresenceInfo([], 0);

    public Task LeaveAsync(Guid nodeId) =>
        http.PostAsync($"/api/nodes/{nodeId}/presence/leave", null);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query) =>
        await http.GetFromJsonAsync<List<SearchHit>>(
            $"/api/search?query={Uri.EscapeDataString(query)}&limit=8") ?? [];

    public async Task<byte[]?> GetImageAsync(string url)
    {
        if (!url.StartsWith("/api/files/", StringComparison.Ordinal))
            return null;
        var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
    }

    private record SaveResult(int Version);
}
