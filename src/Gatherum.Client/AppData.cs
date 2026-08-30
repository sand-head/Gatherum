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

    /// <summary>A body as a reader needs it, without the heartbeat a load carries:
    /// opening a page to read it is not editing it, and a visitor with no session has no
    /// presence to announce and no version to race.</summary>
    Task<string> ReadTextAsync(Guid nodeId);
    Task<byte[]> ReadBytesAsync(Guid nodeId);

    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit);

    /// <summary>Which of these titles name a node the user can see. A [[wiki link]]
    /// addresses a page by name, so this is what it has to ask before it can go
    /// anywhere — and what tells the editor which links are still red.</summary>
    Task<IReadOnlyDictionary<string, Guid>> ResolveTitlesAsync(IReadOnlyList<string> titles);

    /// <summary>Which of these nodes the reader may open. The ids are already in the
    /// page — a mention or an embedded file names one outright — so the answer decides
    /// how the link is drawn, not what the page is allowed to say.</summary>
    Task<IReadOnlySet<Guid>> ReachableNodesAsync(IReadOnlyList<Guid> nodeIds);

    /// <summary>Bytes for an image a document embeds. Only in-app content URLs
    /// (/api/files/…/content) resolve; anything else stays a placeholder.</summary>
    Task<byte[]?> GetImageAsync(string url);

    // The tree.
    Task<IReadOnlyList<TreeNodeInfo>> GetTreeAsync();
    Task<Guid> CreatePageAsync(Guid? parentId, string title);
    Task<Guid> UploadFileAsync(Guid? parentId, string fileName, string contentType, Stream content);

    /// <summary>Bookmark a web page: the URL is fetched now and kept as a snapshot
    /// file node. Slow by nature — it is somebody else's server — so callers show
    /// progress rather than assuming this returns like a local write.</summary>
    Task<Guid> BookmarkAsync(Guid? parentId, string url);

    /// <summary>Fetch a bookmark's URL again; what comes back is a new version.</summary>
    Task CaptureBookmarkAsync(Guid nodeId);
    Task MoveAsync(Guid nodeId, Guid? newParentId, int? position = null);
    Task RenameAsync(Guid nodeId, string title);
    Task DeleteAsync(Guid nodeId);
    Task SetAccessAsync(Guid nodeId, string access);
    Task<IReadOnlyList<GrantInfo>> ListGrantsAsync(Guid nodeId);
    Task<IReadOnlyList<PersonInfo>> ListPeopleAsync();
    Task ShareAsync(Guid nodeId, Guid userId, string role);
    Task UnshareAsync(Guid nodeId, Guid userId);

    /// <summary>A collectible list as everyone's ticks make it, asked of whichever page
    /// the reader is on — a catalogue aggregates itself, a tally aggregates the catalogue
    /// it tracks. <paramref name="list"/> is the fence's own argument, which is what
    /// tells two lists on one page apart.</summary>
    Task<CollectionInfo> GetCollectionAsync(Guid nodeId, string? list);

    /// <summary>Records one collectible against the reader's own tally — written into
    /// being the first time they tick anything — and answers with the list again.</summary>
    Task<CollectionInfo> SetCollectedAsync(Guid nodeId, string key, bool collected, string? list);

    // A node's chrome: categories, file facts, history.
    Task<NodeInfo> GetNodeAsync(Guid nodeId);
    Task<IReadOnlyList<RelatedInfo>> GetSimilarAsync(Guid nodeId, int limit);
    Task<IReadOnlyList<CategoryInfo>> ListCategoriesAsync(string? matching = null);

    /// <summary>Files the node under a category, writing its page if nothing is called
    /// they are new; answers with the path it landed on.</summary>
    Task<string> AddCategoryAsync(Guid nodeId, string name);
    Task RemoveCategoryAsync(Guid nodeId, string name);
    Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(Guid nodeId);

    /// <summary>The reader's map of an EPUB node: the book's own title and its chapters
    /// in reading order, named the way its table of contents names them — null where it
    /// names nothing — and where this reader left off, if they are signed in and ever
    /// stopped anywhere.</summary>
    Task<EpubInfo> GetEpubAsync(Guid nodeId);

    /// <summary>One chapter as the self-contained page the reader's frame shows —
    /// fetched by the component and handed to the frame as srcdoc, never navigated to,
    /// because iOS withholds the touch stream from a network-src sandboxed frame. The
    /// version and renderer keys make the fetch cache-safe forever.</summary>
    Task<string> GetEpubChapterAsync(Guid nodeId, int chapter, int version, string renderer);

    /// <summary>Remembers where the reader is in a book, so coming back — on any
    /// device — reopens it there.</summary>
    Task SaveEpubPositionAsync(Guid nodeId, int chapter, double progress);
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
    string Kind, int Position, string Access, string Reach, bool ListedToSignedIn, bool Owned);
/// <summary>Somebody this node is shared with, and what they may do.</summary>
public record GrantInfo(Guid UserId, string DisplayName, string Username, string Role);

/// <summary>Somebody who could be shared with.</summary>
public record PersonInfo(Guid Id, string DisplayName, string Username);

public record NodeInfo(Guid Id, string Title, string Access,
    IReadOnlyList<CategoryRef> Categories, FileFacts? File);
public record FileFacts(string FileName, string MediaType, long SizeBytes, int Version,
    string Sha256, string Description, string? SourceUrl, string ExtractedText,
    string Transcript, string Summary, string Analysis, string? AnalysisError)
{
    public bool AnalysisPending => Analysis == "Pending";
    public bool AnalysisFailed => Analysis == "Failed";
}
public record VersionInfo(int Number, string FileName, string MediaType, long SizeBytes,
    DateTimeOffset UploadedAt, bool IsText);
/// <summary>Version and Renderer are the two keys a chapter URL pins, so a browser may
/// cache chapters hard without ever holding a stale one: the book's version changes
/// when the file does, the renderer stamp when the reader itself does.</summary>
public record EpubInfo(string? Title, IReadOnlyList<string?> Chapters,
    EpubPosition? Position, int Version, string Renderer);
/// <summary>A ribbon in a book: the chapter, and how far through it (0..1).</summary>
public record EpubPosition(int Chapter, double Progress);
/// <summary>A category as a node wears it: the id is the page it is, the name is what
/// the chip says.</summary>
public record CategoryRef(Guid Id, string Name);
public record CategoryInfo(Guid Id, string Name, IReadOnlyList<Guid> ParentIds, int Members,
    int SubtreeMembers);
public record RelatedInfo(Guid Id, string Kind, string Title);

/// <summary>A collectible list: the catalogue's rows in the author's order, one column
/// per tally this reader may enumerate, and which of them is theirs.</summary>
public record CollectionInfo(Guid CatalogueId, string CatalogueTitle, string List,
    IReadOnlyList<CollectionRowInfo> Rows, IReadOnlyList<CollectionColumnInfo> Columns,
    Guid? TallyId, bool CanTick, int Collectibles);

/// <summary>One line of the catalogue. A row with variants is a group and is not itself
/// tickable: "give me all three" is a different statement from the three ticks.</summary>
public record CollectionRowInfo(string Key, string Text, Guid? NodeId, string Note,
    IReadOnlyList<CollectionRowInfo> Variants);

public record CollectionColumnInfo(Guid TallyId, Guid OwnerId, string DisplayName,
    bool IsViewer, string Access, IReadOnlyList<string> Held,
    IReadOnlyList<CollectionOrphanInfo> Orphans, int Count);

/// <summary>A tick that no longer matches an item, because the catalogue was edited
/// under it. Shown rather than swallowed.</summary>
public record CollectionOrphanInfo(string Text, string Note);
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

    public Task<string> ReadTextAsync(Guid nodeId) =>
        http.GetStringAsync($"/api/files/{nodeId}/content");

    public Task<byte[]> ReadBytesAsync(Guid nodeId) =>
        http.GetByteArrayAsync($"/api/files/{nodeId}/content");

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

    public async Task<IReadOnlySet<Guid>> ReachableNodesAsync(IReadOnlyList<Guid> nodeIds)
    {
        if (nodeIds.Count == 0)
            return new HashSet<Guid>();
        var response = await http.PostAsJsonAsync("/api/nodes/reachable", new { ids = nodeIds });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<Guid>>() ?? []).ToHashSet();
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

    public async Task<Guid> BookmarkAsync(Guid? parentId, string url)
    {
        var response = await http.PostAsJsonAsync("/api/bookmarks", new { url, parentId });
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<NodeInfo>())!.Id;
    }

    public async Task CaptureBookmarkAsync(Guid nodeId) =>
        await EnsureAsync(await http.PostAsync($"/api/bookmarks/{nodeId}/capture", null));

    public async Task MoveAsync(Guid nodeId, Guid? newParentId, int? position = null) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/move", new { newParentId, position }));

    public async Task RenameAsync(Guid nodeId, string title) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/rename", new { title }));

    public async Task DeleteAsync(Guid nodeId) =>
        Ensure(await http.DeleteAsync($"/api/nodes/{nodeId}"));

    public async Task SetAccessAsync(Guid nodeId, string access) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/access", new { access }));

    public async Task<IReadOnlyList<GrantInfo>> ListGrantsAsync(Guid nodeId) =>
        (await http.GetFromJsonAsync<List<GrantInfo>>($"/api/nodes/{nodeId}/grants"))!;

    public async Task<IReadOnlyList<PersonInfo>> ListPeopleAsync() =>
        (await http.GetFromJsonAsync<List<PersonInfo>>("/api/users"))!;

    public async Task ShareAsync(Guid nodeId, Guid userId, string role) =>
        Ensure(await http.PostAsJsonAsync($"/api/nodes/{nodeId}/grants", new { userId, role }));

    public async Task UnshareAsync(Guid nodeId, Guid userId) =>
        Ensure(await http.DeleteAsync($"/api/nodes/{nodeId}/grants/{userId}"));

    public async Task<CollectionInfo> GetCollectionAsync(Guid nodeId, string? list) =>
        (await http.GetFromJsonAsync<CollectionInfo>(
            $"/api/nodes/{nodeId}/collection{ListQuery(list)}"))!;

    public async Task<CollectionInfo> SetCollectedAsync(Guid nodeId, string key, bool collected,
        string? list)
    {
        var response = await http.PostAsJsonAsync($"/api/nodes/{nodeId}/collection",
            new { key, collected, list });
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<CollectionInfo>())!;
    }

    private static string ListQuery(string? list) =>
        list is { Length: > 0 } ? $"?list={Uri.EscapeDataString(list)}" : "";

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

    public async Task<string> AddCategoryAsync(Guid nodeId, string name)
    {
        var response = await http.PostAsJsonAsync($"/api/nodes/{nodeId}/categories", new { name });
        await EnsureAsync(response);
        return (await response.Content.ReadFromJsonAsync<CategoryPlacement>())?.Name ?? name;
    }

    public async Task RemoveCategoryAsync(Guid nodeId, string name) =>
        await EnsureAsync(await http.DeleteAsync(
            $"/api/nodes/{nodeId}/categories/{CategoryUrl.For(name)}"));

    public async Task<EpubInfo> GetEpubAsync(Guid nodeId) =>
        (await http.GetFromJsonAsync<EpubInfo>($"/api/files/{nodeId}/epub"))!;

    public async Task<string> GetEpubChapterAsync(Guid nodeId, int chapter, int version,
        string renderer) =>
        await http.GetStringAsync(
            $"/api/files/{nodeId}/epub/{chapter}?version={version}&r={renderer}");

    public async Task SaveEpubPositionAsync(Guid nodeId, int chapter, double progress) =>
        (await http.PutAsJsonAsync($"/api/files/{nodeId}/epub/position",
            new { chapter, progress })).EnsureSuccessStatusCode();

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
    private record CategoryPlacement(string Name);
    private record ApiProblem(string? Error, string? Detail);
}
