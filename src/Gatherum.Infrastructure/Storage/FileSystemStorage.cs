using System.Security.Cryptography;
using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure.Storage;

/// <summary>Stores blobs on disk under root/ab/cd/abcd… (SHA-256, fanned out two levels
/// so no directory grows unbounded). Identical content lands on the same path, which
/// makes writes idempotent and deduplicates re-uploads for free.</summary>
public class FileSystemStorage(IOptions<GatherumOptions> options) : IFileStorage
{
    private readonly string root = Path.GetFullPath(options.Value.Storage.Root);

    public async Task<StoredBlob> SaveAsync(Stream content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(root);
        var temp = Path.Combine(root, $"incoming-{Guid.NewGuid():N}");
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

            var final = PathFor(hash);
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

    public Task<Stream> OpenReadAsync(string hash, CancellationToken cancellationToken = default)
    {
        var path = PathFor(hash);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Blob {hash} is not in storage.", path);
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(PathFor(hash)));

    private string PathFor(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("Not a SHA-256 hex digest.", nameof(hash));
        return Path.Combine(root, hash[..2], hash[2..4], hash);
    }
}
