using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Gatherum.Client;

/// <summary>The interactive components' view of the server, abstracted because
/// Interactive Auto components live in two homes: rendered on the server circuit they
/// get a direct service-backed implementation, rendered in WebAssembly they get this
/// project's HTTP one. The components themselves never know which.</summary>
public interface IAppData
{
    /// <summary>One ceiling for every upload path: the components' stream reads and
    /// the server's request-body limits both quote it.</summary>
    const long MaxUploadBytes = 512L * 1024 * 1024;

    // The editor.
    Task<EditorPayload> LoadAsync(Guid nodeId);
    Task<int> SaveTextAsync(Guid nodeId, string text);
    Task<BytesPayload> LoadBytesAsync(Guid nodeId);
    Task<int> SaveBytesAsync(Guid nodeId, byte[] content);
    Task<PresenceInfo> HeartbeatAsync(Guid nodeId);
    Task LeaveAsync(Guid nodeId);
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit);

    /// <summary>Which of these titles name a node the user can see. A [[wiki link]]
    /// addresses a page by name, so this is what it has to ask before it can go
    /// anywhere — and what tells the editor which links are still red.</summary>
    Task<IReadOnlyDictionary<string, Guid>> ResolveTitlesAsync(IReadOnlyList<string> titles);

    /// <summary>Bytes for an image a document embeds. Only in-app content URLs
    /// (/api/files/…/content) resolve; anything else stays a placeholder.</summary>
    Task<byte[]?> GetImageAsync(string url);

    // The tree.
    Task<IReadOnlyList<TreeNodeInfo>> GetTreeAsync();
    Task<Guid> CreatePageAsync(Guid? parentId, string title);
    Task<Guid> UploadFileAsync(Guid? parentId, string fileName, string contentType, Stream content);
    Task MoveAsync(Guid nodeId, Guid? newParentId, int? position = null);
    Task RenameAsync(Guid nodeId, string title);
    Task DeleteAsync(Guid nodeId);
    Task SetPrivateAsync(Guid nodeId, bool isPrivate);

    // A node's chrome: categories, file facts, history.
    Task<NodeInfo> GetNodeAsync(Guid nodeId);
    Task<IReadOnlyList<RelatedInfo>> GetSimilarAsync(Guid nodeId, int limit);
    Task<IReadOnlyList<CategoryInfo>> ListCategoriesAsync(string? matching = null);

    /// <summary>Files the node under a category path, creating it and its ancestors if
    /// they are new; answers with the path it landed on.</summary>
    Task<string> AddCategoryAsync(Guid nodeId, string path);
    Task RemoveCategoryAsync(Guid nodeId, string path);

    // Maintaining the taxonomy itself: a category follows its subcategories around.
    Task RenameCategoryAsync(string path, string name);
    Task MoveCategoryAsync(string path, string? newParentPath);
    Task DeleteCategoryAsync(string path);
    Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(Guid nodeId);
    Task<string> GetVersionTextAsync(Guid nodeId, int number);
    Task RestoreVersionAsync(Guid nodeId, int number);
    Task UploadVersionAsync(Guid nodeId, string fileName, string contentType, Stream content);
    Task SetDescriptionAsync(Guid nodeId, string description);

    // Settings.
    Task<IReadOnlyList<KeyInfo>> ListKeysAsync();
    Task<CreatedKey> CreateKeyAsync(string name);
    Task RevokeKeyAsync(Guid keyId);
}

public record EditorPayload(string Text, int HeadVersion);
public record BytesPayload(byte[] Content, int HeadVersion);
public record PresenceInfo(IReadOnlyList<string> Editors, int HeadVersion);
public record SearchHit(Guid Id, string Kind, string Title, string Snippet);
public record TitleMatch(string Title, Guid Id);
public record TreeNodeInfo(Guid Id, Guid? ParentId, string Title, string MediaType,
    string Kind, int Position, bool IsPrivate);
public record NodeInfo(Guid Id, string Title, bool IsPrivate,
    IReadOnlyList<CategoryRef> Categories, FileFacts? File);
public record FileFacts(string FileName, string MediaType, long SizeBytes, int Version,
    string Sha256, string Description, string ExtractedText, string Transcript, string Summary,
    string Analysis, string? AnalysisError)
{
    public bool AnalysisPending => Analysis == "Pending";
    public bool AnalysisFailed => Analysis == "Failed";
}
public record VersionInfo(int Number, string FileName, string MediaType, long SizeBytes,
    DateTimeOffset UploadedAt, bool IsText);
/// <summary>A category as a node wears it: the path is what it is, the name is what
/// the chip says.</summary>
public record CategoryRef(string Path, string Name);
public record CategoryInfo(string Path, string Name, string? ParentPath, int Members,
    int SubtreeMembers);
public record RelatedInfo(Guid Id, string Kind, string Title);
public record KeyInfo(Guid Id, string Name, string Prefix, DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt, bool IsActive);
public record CreatedKey(Guid Id, string Name, string Token);

public sealed class HttpAppData(HttpClient http) : IAppData
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

    public async Task<BytesPayload> LoadBytesAsync(Guid nodeId)
    {
        var bytes = await http.GetByteArrayAsync($"/api/files/{nodeId}/content");
        var presence = await HeartbeatAsync(nodeId);
        return new BytesPayload(bytes, presence.HeadVersion);
    }

    public async Task<int> SaveBytesAsync(Guid nodeId, byte[] content)
    {
        var response = await http.PutAsync($"/api/binary/{nodeId}", new ByteArrayContent(content));
        response.EnsureSuccessStatusCode();
        var saved = await response.Content.ReadFromJsonAsync<SaveResult>();
        return saved?.Version ?? 0;
    }

    public async Task<PresenceInfo> HeartbeatAsync(Guid nodeId) =>
        await http.GetFromJsonAsync<PresenceInfo>($"/api/nodes/{nodeId}/presence?editing=true")
            ?? new PresenceInfo([], 0);

    public Task LeaveAsync(Guid nodeId) =>
        http.PostAsync($"/api/nodes/{nodeId}/presence/leave", null);

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit) =>
        await http.GetFromJsonAsync<List<SearchHit>>(
            $"/api/search?query={Uri.EscapeDataString(query)}&limit={limit}") ?? [];

    public async Task<IReadOnlyDictionary<string, Guid>> ResolveTitlesAsync(
        IReadOnlyList<string> titles)
    {
        if (titles.Count == 0)
            return new Dictionary<string, Guid>();
        var response = await http.PostAsJsonAsync("/api/nodes/resolve-titles", new { titles });
        response.EnsureSuccessStatusCode();
        var matches = await response.Content.ReadFromJsonAsync<List<TitleMatch>>() ?? [];
        return matches.ToDictionary(m => m.Title, m => m.Id, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<byte[]?> GetImageAsync(string url)
    {
        if (!url.StartsWith("/api/files/", StringComparison.Ordinal))
            return null;
        var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync() : null;
    }

    public async Task<IReadOnlyList<TreeNodeInfo>> GetTreeAsync() =>
        await http.GetFromJsonAsync<List<TreeNodeInfo>>("/api/nodes/tree") ?? [];

    public async Task<Guid> CreatePageAsync(Guid? parentId, string title)
    {
        var response = await http.PostAsJsonAsync("/api/pages", new { parentId, title });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeInfo>())!.Id;
    }

    public async Task<Guid> UploadFileAsync(Guid? parentId, string fileName, string contentType,
        Stream content)
    {
        var url = parentId is { } id ? $"/api/files?parentId={id}" : "/api/files";
        var response = await http.PostAsync(url, FilePart(fileName, contentType, content));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<NodeInfo>())!.Id;
    }

    public async Task MoveAsync(Guid nodeId, Guid? newParentId, int? position = null) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/move", new { newParentId, position }));

    public async Task RenameAsync(Guid nodeId, string title) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/rename", new { title }));

    public async Task DeleteAsync(Guid nodeId) =>
        Ensure(await http.DeleteAsync($"/api/nodes/{nodeId}"));

    public async Task SetPrivateAsync(Guid nodeId, bool isPrivate) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/private", new { isPrivate }));

    public async Task<NodeInfo> GetNodeAsync(Guid nodeId) =>
        (await http.GetFromJsonAsync<NodeInfo>($"/api/nodes/{nodeId}"))!;

    public async Task<IReadOnlyList<RelatedInfo>> GetSimilarAsync(Guid nodeId, int limit) =>
        await http.GetFromJsonAsync<List<RelatedInfo>>(
            $"/api/nodes/{nodeId}/similar?limit={limit}") ?? [];

    public async Task<IReadOnlyList<CategoryInfo>> ListCategoriesAsync(string? matching = null)
    {
        var url = matching is { Length: > 0 }
            ? $"/api/categories?matching={Uri.EscapeDataString(matching)}"
            : "/api/categories";
        return await http.GetFromJsonAsync<List<CategoryInfo>>(url) ?? [];
    }

    public async Task<string> AddCategoryAsync(Guid nodeId, string path)
    {
        var response = await http.PostAsJsonAsync($"/api/nodes/{nodeId}/categories", new { path });
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<CategoryPlacement>())?.Path ?? path;
    }

    public async Task RemoveCategoryAsync(Guid nodeId, string path) =>
        await EnsureAsync(await http.DeleteAsync(
            $"/api/nodes/{nodeId}/categories/{CategoryUrl.For(path)}"));

    public async Task RenameCategoryAsync(string path, string name) =>
        await EnsureAsync(await http.PostAsJsonAsync("/api/categories/rename",
            new { path, name }));

    public async Task MoveCategoryAsync(string path, string? newParentPath) =>
        await EnsureAsync(await http.PostAsJsonAsync("/api/categories/move",
            new { path, newParentPath }));

    public async Task DeleteCategoryAsync(string path) =>
        await EnsureAsync(await http.DeleteAsync($"/api/categories/{CategoryUrl.For(path)}"));

    public async Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(Guid nodeId) =>
        await http.GetFromJsonAsync<List<VersionInfo>>($"/api/nodes/{nodeId}/versions") ?? [];

    public Task<string> GetVersionTextAsync(Guid nodeId, int number) =>
        http.GetStringAsync($"/api/files/{nodeId}/content?version={number}");

    public async Task RestoreVersionAsync(Guid nodeId, int number) =>
        Ensure(await http.PostAsync($"/api/nodes/{nodeId}/versions/{number}/restore", null));

    public async Task UploadVersionAsync(Guid nodeId, string fileName, string contentType,
        Stream content) =>
        Ensure(await http.PostAsync($"/api/files/{nodeId}/versions",
            FilePart(fileName, contentType, content)));

    public async Task SetDescriptionAsync(Guid nodeId, string description) =>
        Ensure(await http.PutAsJsonAsync($"/api/files/{nodeId}/description", new { description }));

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync() =>
        await http.GetFromJsonAsync<List<KeyInfo>>("/api/keys") ?? [];

    public async Task<CreatedKey> CreateKeyAsync(string name)
    {
        var response = await http.PostAsJsonAsync("/api/keys", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedKey>())!;
    }

    public async Task RevokeKeyAsync(Guid keyId) =>
        Ensure(await http.DeleteAsync($"/api/keys/{keyId}"));

    private static MultipartFormDataContent FilePart(string fileName, string contentType,
        Stream content)
    {
        var part = new StreamContent(content);
        if (contentType.Length > 0)
            part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { part, "file", fileName } };
    }

    private static void Ensure(HttpResponseMessage response) => response.EnsureSuccessStatusCode();

    /// <summary>Like <see cref="Ensure"/>, but keeps what the server said: the API
    /// answers a refused category with a sentence a person can read, and the components
    /// show it rather than a status code.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var problem = await response.Content.ReadFromJsonAsync<ApiProblem>();
        var message = problem?.Error ?? problem?.Detail;
        if (message is { Length: > 0 })
            throw new InvalidOperationException(message);
        response.EnsureSuccessStatusCode();
    }

    private record SaveResult(int Version);
    private record CategoryPlacement(string Path);
    private record ApiProblem(string? Error, string? Detail);
}
