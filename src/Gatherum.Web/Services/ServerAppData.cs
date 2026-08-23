using Gatherum.Client;
using Gatherum.Core.Domain;
using Gatherum.Core.Markdown;
using Gatherum.Web.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace Gatherum.Web.Services;

/// <summary>The interactive components' data source while they render on the server:
/// same contract as the WASM HTTP implementation, but straight into the application
/// services under the circuit's authenticated user.</summary>
public sealed class ServerAppData(
    AppOperations ops,
    PresenceTracker presence,
    AuthenticationStateProvider authentication) : IAppData
{
    public async Task<EditorPayload> LoadAsync(Guid nodeId)
    {
        var userId = await ViewerIdAsync();
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

    public async Task<BytesPayload> LoadBytesAsync(Guid nodeId)
    {
        var userId = await ViewerIdAsync();
        var head = await ops.Files(s => s.GetHeadVersionAsync(userId, nodeId));
        var content = await ops.Files(s => s.OpenContentAsync(userId, nodeId));
        await using (content.Stream)
        {
            using var buffer = new MemoryStream();
            await content.Stream.CopyToAsync(buffer);
            return new BytesPayload(buffer.ToArray(), head);
        }
    }

    public async Task<int> SaveBytesAsync(Guid nodeId, byte[] content)
    {
        var userId = await UserIdAsync();
        var version = await ops.Files(s => s.SaveBinaryAsync(userId, nodeId, content));
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

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit)
    {
        var userId = await ViewerIdAsync();
        var results = await ops.Search(s => s.SearchAsync(userId, query, limit: limit));
        return results
            .Select(r => new SearchHit(r.Id, r.Kind.ToString(), r.Title, r.Snippet))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, Guid>> ResolveTitlesAsync(
        IReadOnlyList<string> titles)
    {
        var userId = await ViewerIdAsync();
        return await ops.Nodes(s => s.ResolveTitlesAsync(userId, titles));
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

    public async Task<IReadOnlyList<TreeNodeInfo>> GetTreeAsync()
    {
        var userId = await ViewerIdAsync();
        var tree = await ops.Nodes(s => s.GetTreeAsync(userId));
        return tree
            .Select(n => new TreeNodeInfo(n.Id, n.ParentId, n.Title, n.MediaType,
                n.Kind.ToString(), n.Position, n.Access.ToString(), n.Reach.ToString(), n.Owned))
            .ToList();
    }

    public async Task<Guid> CreatePageAsync(Guid? parentId, string title)
    {
        var userId = await UserIdAsync();
        var node = await ops.Files(s => s.CreateTextNodeAsync(userId, parentId, title));
        return node.Id;
    }

    public async Task<Guid> UploadFileAsync(Guid? parentId, string fileName, string contentType,
        Stream content)
    {
        var userId = await UserIdAsync();
        var node = await ops.Files(s =>
            s.CreateFileNodeAsync(userId, parentId, fileName, contentType, content));
        return node.Id;
    }

    public async Task MoveAsync(Guid nodeId, Guid? newParentId, int? position = null)
    {
        var userId = await UserIdAsync();
        await ops.Nodes(s => s.MoveAsync(userId, nodeId, newParentId, position));
    }

    public async Task RenameAsync(Guid nodeId, string title)
    {
        var userId = await UserIdAsync();
        await ops.Nodes(s => s.RenameAsync(userId, nodeId, title));
    }

    public async Task DeleteAsync(Guid nodeId)
    {
        var userId = await UserIdAsync();
        await ops.Nodes(s => s.DeleteAsync(userId, nodeId));
    }

    public async Task SetAccessAsync(Guid nodeId, string access)
    {
        var userId = await UserIdAsync();
        var mode = Enum.Parse<AccessMode>(access, ignoreCase: true);
        await ops.Access(s => s.SetAccessAsync(userId, nodeId, mode));
    }

    public async Task ShareAsync(Guid nodeId, Guid userId, string role)
    {
        var actor = await UserIdAsync();
        var parsed = Enum.Parse<AccessRole>(role, ignoreCase: true);
        await ops.Access(s => s.GrantAsync(actor, nodeId, userId, parsed));
    }

    public async Task UnshareAsync(Guid nodeId, Guid userId)
    {
        var actor = await UserIdAsync();
        await ops.Access(s => s.RevokeAsync(actor, nodeId, userId));
    }

    public async Task<NodeInfo> GetNodeAsync(Guid nodeId)
    {
        var userId = await ViewerIdAsync();
        var node = await ops.Nodes(s => s.GetWithBodyAsync(userId, nodeId));
        var file = node.File is { Versions.Count: > 0 } body
            ? new FileFacts(body.Current.FileName, body.Current.MediaType,
                body.Current.SizeBytes, body.Current.Number, body.Current.Hash,
                body.Description, body.Current.ExtractedText, body.Current.Transcript,
                body.Current.Summary, body.Current.Analysis.ToString(),
                body.Current.AnalysisError.Length > 0 ? body.Current.AnalysisError : null)
            : null;
        return new NodeInfo(node.Id, node.Title, node.Access.ToString(),
            node.Categories
                .Select(c => new CategoryRef(c.Category!.Path, c.Category.Name))
                .OrderBy(c => c.Path)
                .ToList(),
            file);
    }

    public async Task<IReadOnlyList<RelatedInfo>> GetSimilarAsync(Guid nodeId, int limit)
    {
        var userId = await ViewerIdAsync();
        var similar = await ops.Nodes(s => s.GetSimilarAsync(userId, nodeId, limit));
        return similar.Select(s => new RelatedInfo(s.Id, s.Kind.ToString(), s.Title)).ToList();
    }

    public async Task<IReadOnlyList<CategoryInfo>> ListCategoriesAsync(string? matching = null)
    {
        var userId = await ViewerIdAsync();
        var categories = await ops.Categories(s => s.ListAsync(userId, matching));
        return categories
            .Select(c => new CategoryInfo(c.Path, c.Name, c.ParentPath, c.Members,
                c.SubtreeMembers))
            .ToList();
    }

    public async Task<string> AddCategoryAsync(Guid nodeId, string path)
    {
        var userId = await UserIdAsync();
        return await ops.Categories(s => s.AddAsync(userId, nodeId, path));
    }

    public async Task RemoveCategoryAsync(Guid nodeId, string path)
    {
        var userId = await UserIdAsync();
        await ops.Categories(s => s.RemoveAsync(userId, nodeId, path));
    }

    public Task RenameCategoryAsync(string path, string name) =>
        ops.Categories(s => s.RenameAsync(path, name));

    public Task MoveCategoryAsync(string path, string? newParentPath) =>
        ops.Categories(s => s.MoveAsync(path, newParentPath));

    public Task DeleteCategoryAsync(string path) =>
        ops.Categories(s => s.DeleteAsync(path));

    public async Task<IReadOnlyList<VersionInfo>> GetVersionsAsync(Guid nodeId)
    {
        var userId = await ViewerIdAsync();
        var node = await ops.Nodes(s => s.GetWithBodyAsync(userId, nodeId));
        return (node.File?.Versions ?? [])
            .OrderByDescending(v => v.Number)
            .Select(v => new VersionInfo(v.Number, v.FileName, v.MediaType, v.SizeBytes,
                v.UploadedAt, MediaTypes.IsText(v.MediaType, v.FileName)))
            .ToList();
    }

    public async Task<string> GetVersionTextAsync(Guid nodeId, int number)
    {
        var userId = await ViewerIdAsync();
        var content = await ops.Files(s => s.OpenContentAsync(userId, nodeId, number));
        await using (content.Stream)
        {
            using var reader = new StreamReader(content.Stream);
            return await reader.ReadToEndAsync();
        }
    }

    public async Task RestoreVersionAsync(Guid nodeId, int number)
    {
        var userId = await UserIdAsync();
        await ops.Files(s => s.RestoreVersionAsync(userId, nodeId, number));
    }

    public async Task UploadVersionAsync(Guid nodeId, string fileName, string contentType,
        Stream content)
    {
        var userId = await UserIdAsync();
        await ops.Files(s => s.UploadVersionAsync(userId, nodeId, fileName, contentType, content));
    }

    public async Task SetDescriptionAsync(Guid nodeId, string description)
    {
        var userId = await UserIdAsync();
        await ops.Files(s => s.SetDescriptionAsync(userId, nodeId, description));
    }

    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync()
    {
        var userId = await UserIdAsync();
        var keys = await ops.Keys(s => s.ListAsync(userId));
        return keys
            .Select(k => new KeyInfo(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt, k.IsActive))
            .ToList();
    }

    public async Task<CreatedKey> CreateKeyAsync(string name)
    {
        var userId = await UserIdAsync();
        var created = await ops.Keys(s => s.CreateAsync(userId, name));
        return new CreatedKey(created.Key.Id, created.Key.Name, created.PlaintextToken);
    }

    public async Task RevokeKeyAsync(Guid keyId)
    {
        var userId = await UserIdAsync();
        await ops.Keys(s => s.RevokeAsync(userId, keyId));
    }

    /// <summary>The signed-in user, for anything that writes. Anonymous callers have no
    /// business here and get an exception rather than a silent no-op.</summary>
    private async Task<Guid> UserIdAsync() =>
        (await authentication.GetAuthenticationStateAsync()).User.GetUserId();

    /// <summary>Who is looking, or null for somebody who has not signed in. Reads take
    /// this so a public page renders for a stranger, and the authorizer decides the rest —
    /// the same nullable that already runs through every service beneath here.</summary>
    private async Task<Guid?> ViewerIdAsync() =>
        (await authentication.GetAuthenticationStateAsync()).User.GetUserIdOrNull();
}
