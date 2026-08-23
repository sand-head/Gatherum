using System.Security.Cryptography;
using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure.Storage;

/// <summary>The filesystem, as the system of record. Current content is a plain file at
/// <c>root/{owner}/{path}</c>; superseded content goes to <c>{owner}/.gatherum/versions/ab/cd/…</c>,
/// still content-addressed because dedup across versions and restore-as-a-row are worth
/// keeping — the hash simply stops being the namespace.
///
/// Nothing is served whose real path escapes the root it was found under: user-controlled
/// names and symlinks are where a path-addressed store gets exploited, and the check is
/// cheap enough to make unconditionally.</summary>
public class FileSystemStorage(IOptions<GatherumOptions> options) : IFileStorage
{
    /// <summary>Gatherum's own bookkeeping, and the one directory name a scan skips.</summary>
    public const string SidecarName = ".gatherum";

    private readonly string root = Path.GetFullPath(options.Value.Storage.Root);

    public async Task<StoredBlob> WriteAsync(NodePath path, Stream content,
        CancellationToken cancellationToken = default)
    {
        var target = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + $".incoming-{Guid.NewGuid():N}";
        try
        {
            long size;
            string hash;
            await using (var file = File.Create(temp))
            {
                using var sha = SHA256.Create();
                await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write,
                    leaveOpen: true))
                {
                    await content.CopyToAsync(hashing, cancellationToken);
                }
                size = file.Length;
                hash = Convert.ToHexStringLower(sha.Hash!);
            }
            File.Move(temp, target, overwrite: true);
            return new StoredBlob(hash, size);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(NodePath path, CancellationToken cancellationToken = default)
    {
        var target = Resolve(path);
        if (!File.Exists(target))
            throw new FileNotFoundException($"No file at {path}.", target);
        return Task.FromResult<Stream>(File.OpenRead(target));
    }

    public Task<bool> ExistsAsync(NodePath path, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Resolve(path)));

    public Task MoveAsync(NodePath from, NodePath to, CancellationToken cancellationToken = default)
    {
        var source = Resolve(from);
        var target = Resolve(to);
        if (string.Equals(source, target, StringComparison.Ordinal))
            return Task.CompletedTask;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, overwrite: false);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(NodePath path, CancellationToken cancellationToken = default)
    {
        File.Delete(Resolve(path));
        return Task.CompletedTask;
    }

    public async Task<StoredBlob> MeasureAsync(NodePath path,
        CancellationToken cancellationToken = default)
    {
        var target = Resolve(path);
        await using var file = File.OpenRead(target);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(file, cancellationToken);
        return new StoredBlob(Convert.ToHexStringLower(hash), file.Length);
    }

    public async Task<StoredBlob> ArchiveAsync(string root, Stream content,
        CancellationToken cancellationToken = default)
    {
        var versions = VersionsDirectory(root);
        Directory.CreateDirectory(versions);
        var temp = Path.Combine(versions, $"incoming-{Guid.NewGuid():N}");
        try
        {
            long size;
            string hash;
            await using (var file = File.Create(temp))
            {
                using var sha = SHA256.Create();
                await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write,
                    leaveOpen: true))
                {
                    await content.CopyToAsync(hashing, cancellationToken);
                }
                size = file.Length;
                hash = Convert.ToHexStringLower(sha.Hash!);
            }

            var final = ArchivePath(root, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            if (File.Exists(final))
                File.Delete(temp);
            else
                File.Move(temp, final);
            return new StoredBlob(hash, size);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public Task<Stream> OpenArchiveAsync(string root, string hash,
        CancellationToken cancellationToken = default)
    {
        var path = ArchivePath(root, hash);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Archived blob {hash} is not in storage.", path);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<bool> ArchivedAsync(string root, string hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(ArchivePath(root, hash)));

    public IEnumerable<NodePath> Walk(string root)
    {
        var start = RootDirectory(root);
        if (!Directory.Exists(start))
            yield break;

        foreach (var file in Directory.EnumerateFiles(start, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(start, file).Replace(Path.DirectorySeparatorChar, '/');
            // Gatherum's own bookkeeping is not content, and a half-written upload is not
            // yet content either.
            if (relative.Split('/').Contains(SidecarName) || relative.Contains(".incoming-"))
                continue;
            // A symlink out of the root would launder one user's file into another's
            // ownership, since ownership is the directory. Refuse rather than index.
            if (!IsInside(start, file))
                continue;
            yield return new NodePath(root, relative);
        }
    }

    public IEnumerable<string> Roots()
    {
        if (!Directory.Exists(root))
            return [];
        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is { Length: > 0 } && name != SidecarName)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal);
    }

    public string SidecarDirectory(NodePath path)
    {
        var containing = Path.GetDirectoryName(Resolve(path))!;
        return Path.Combine(containing, SidecarName);
    }

    private string RootDirectory(string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name == ".."
            || name == SidecarName)
            throw new ArgumentException($"'{name}' is not a root directory name.", nameof(name));
        return Path.Combine(root, name);
    }

    private string VersionsDirectory(string root) =>
        Path.Combine(RootDirectory(root), SidecarName, "versions");

    private string ArchivePath(string root, string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("Not a SHA-256 hex digest.", nameof(hash));
        return Path.Combine(VersionsDirectory(root), hash[..2], hash[2..4], hash);
    }

    private string Resolve(NodePath path)
    {
        var start = RootDirectory(path.Root);
        if (path.Relative.Length == 0)
            throw new ArgumentException("A node path needs a file within its root.", nameof(path));
        var segments = path.Relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is ".." or "." || s == SidecarName))
            throw new ArgumentException($"'{path.Relative}' is not a path within a root.", nameof(path));

        var full = Path.GetFullPath(Path.Combine(start, Path.Combine(segments)));
        if (!IsInside(start, full))
            throw new ArgumentException($"'{path}' escapes its root.", nameof(path));
        return full;
    }

    /// <summary>Whether a path is genuinely beneath a root once symlinks are followed.
    /// <see cref="Path.GetFullPath(string)"/> settles <c>..</c> but not links, and the
    /// link is the interesting case: ownership is the directory, so a link that leaves
    /// one is a way to claim somebody else's file.</summary>
    private static bool IsInside(string root, string candidate)
    {
        var real = ResolveLinks(candidate);
        var realRoot = ResolveLinks(root);
        return real == realRoot
            || real.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            // Resolves the deepest existing ancestor, so a file about to be created is
            // still checked against where its directory actually points.
            var existing = path;
            while (existing.Length > 0 && !File.Exists(existing) && !Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null || parent == existing)
                    return Path.GetFullPath(path);
                existing = parent;
            }
            var resolved = Directory.ResolveLinkTarget(existing, returnFinalTarget: true)?.FullName
                ?? File.ResolveLinkTarget(existing, returnFinalTarget: true)?.FullName
                ?? existing;
            return Path.GetFullPath(resolved + path[existing.Length..]);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }
}
