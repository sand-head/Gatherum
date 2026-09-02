using System.Collections.Concurrent;
using Gatherum.Client;

namespace Gatherum.Web.Services;

/// <summary>A file arriving in pieces. A browser cannot stream an upload from .NET: the
/// WebAssembly runtime's HTTP client buffers a request body whole before sending it,
/// and a disc image is bigger than the heap it would have to sit in. So the
/// WebAssembly home sends a file as a run of bounded chunks appended to a staging file
/// here, and the last call hands that file to <c>FileService</c> exactly as a multipart
/// upload would have. The staging file lives in the temp directory, as the multipart
/// path's buffer does; nothing here touches the storage root.
///
/// <para>Who began an upload is the only one who may add to it or finish it, and a
/// staging file nobody has touched for an hour — a tab closed mid-way — is discarded
/// the next time anybody begins one. The map is in memory on purpose: an upload cannot
/// outlive the process it started in, and one that tries gets a 404 to retry from,
/// which is what an interrupted upload is.</para></summary>
public sealed class UploadStaging
{
    public const long Ceiling = IAppData.MaxUploadBytes;
    private static readonly TimeSpan Idle = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, Entry> entries = new();
    private readonly string root = Path.Combine(Path.GetTempPath(), "gatherum-uploads");

    private sealed class Entry(Guid ownerId, string fileName, string contentType, string path)
    {
        public Guid OwnerId { get; } = ownerId;
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;
        public string Path { get; } = path;
        public long Size { get; set; }
        public DateTime Touched { get; set; } = DateTime.UtcNow;
        /// <summary>One chunk at a time: two appends racing would interleave.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    /// <summary>What a finished upload is handed over as: the bytes, which delete
    /// themselves when the stream closes, and what the browser called them.</summary>
    public sealed record Staged(Stream Content, string FileName, string ContentType);

    public UploadStaging()
    {
        Directory.CreateDirectory(root);
        // A previous process's leftovers: nothing remembers them, so nothing can finish them.
        foreach (var stale in new DirectoryInfo(root).EnumerateFiles())
            if (stale.LastWriteTimeUtc < DateTime.UtcNow - Idle)
                TryDelete(stale.FullName);
    }

    public Guid Begin(Guid userId, string fileName, string contentType)
    {
        Sweep();
        var id = Guid.NewGuid();
        var path = Path.Combine(root, id.ToString("N"));
        using (File.Create(path)) { }
        entries[id] = new Entry(userId, fileName, contentType, path);
        return id;
    }

    /// <summary>Appends a chunk. <paramref name="offset"/> is where the sender believes
    /// the file ends, so a chunk sent twice or out of turn is refused rather than written
    /// into the middle of somebody's disc. Null when there is no such upload of this
    /// user's; false when the offset disagrees; throws past the ceiling, which is also
    /// the end of the upload.</summary>
    public async Task<bool?> AppendAsync(Guid userId, Guid id, long offset, Stream chunk,
        CancellationToken ct)
    {
        if (Find(userId, id) is not { } entry)
            return null;
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Size != offset)
                return false;
            bool overflowed;
            await using (var file = new FileStream(entry.Path, FileMode.Append, FileAccess.Write,
                FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await chunk.ReadAsync(buffer, ct)) > 0)
                {
                    if (entry.Size + read > Ceiling)
                        break;
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    entry.Size += read;
                }
                overflowed = read > 0;
            }
            // Past the ceiling with more still coming: the file is closed before it is
            // deleted, and the upload is over.
            if (overflowed)
            {
                Discard(userId, id);
                throw new UploadTooLargeException(Ceiling);
            }
            entry.Touched = DateTime.UtcNow;
            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Takes a finished upload out of staging. The stream is the staging file
    /// itself, deleting on close, so the bytes are gone whether or not what is done
    /// with them succeeds.</summary>
    public Staged? Take(Guid userId, Guid id)
    {
        if (Find(userId, id) is not { } entry || !entries.TryRemove(id, out _))
            return null;
        var content = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.None,
            1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        return new Staged(content, entry.FileName, entry.ContentType);
    }

    public bool Discard(Guid userId, Guid id)
    {
        if (Find(userId, id) is null || !entries.TryRemove(id, out var entry))
            return false;
        TryDelete(entry.Path);
        return true;
    }

    private Entry? Find(Guid userId, Guid id) =>
        entries.TryGetValue(id, out var entry) && entry.OwnerId == userId ? entry : null;

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - Idle;
        foreach (var (id, entry) in entries)
            if (entry.Touched < cutoff && entries.TryRemove(id, out _))
                TryDelete(entry.Path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Still open by an append that is about to fail, or already gone: either
            // way the next sweep sees an orphan and tries again.
        }
    }
}

/// <summary>An upload that grew past <see cref="UploadStaging.Ceiling"/>. Its staging
/// file is already gone by the time this is thrown.</summary>
public sealed class UploadTooLargeException(long ceiling)
    : Exception($"This file is bigger than the {ceiling / (1024 * 1024 * 1024)} GB an upload may be.");
