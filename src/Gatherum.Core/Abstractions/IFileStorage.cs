namespace Gatherum.Core.Abstractions;

/// <summary>Where a node's bytes live: the directory of the user who owns it, and a path
/// beneath it. Ownership is the root — whoever owns the root directory owns everything
/// found under it, and nothing recorded anywhere may disagree.</summary>
/// <param name="Root">The owner's root directory name, relative to the storage root.</param>
/// <param name="Relative">Path within that root, '/'-separated. Never empty, never
/// absolute, never containing a '..' segment.</param>
public readonly record struct NodePath(string Root, string Relative)
{
    public string Name => Relative.Split('/')[^1];

    /// <summary>The title a file has before anybody overrides it: its name without the
    /// extension. This is what makes a directory nobody prepared still read as a wiki.</summary>
    public string DefaultTitle => Path.GetFileNameWithoutExtension(Name) is { Length: > 0 } stem
        ? stem
        : Name;

    public NodePath With(string relative) => this with { Relative = relative };

    public override string ToString() => $"{Root}/{Relative}";
}

/// <summary>The system of record. Current content is a plain file at a plain path — the
/// thing a user would still have if Gatherum vanished — and history is a
/// content-addressed archive beside it, which is Gatherum's own bookkeeping and no
/// loss of the knowledge base if it goes.</summary>
public interface IFileStorage
{
    Task<StoredBlob> WriteAsync(NodePath path, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(NodePath path, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(NodePath path, CancellationToken cancellationToken = default);
    Task MoveAsync(NodePath from, NodePath to, CancellationToken cancellationToken = default);
    Task DeleteAsync(NodePath path, CancellationToken cancellationToken = default);

    /// <summary>Reads what is on disk without writing anything — the hash and length of
    /// the current file, for deciding whether the index still describes it.</summary>
    Task<StoredBlob> MeasureAsync(NodePath path, CancellationToken cancellationToken = default);

    /// <summary>Puts superseded content into the owner's archive. Idempotent by
    /// construction: identical bytes land on the same path, so re-archiving is free and
    /// a reverted document costs nothing twice.</summary>
    Task<StoredBlob> ArchiveAsync(string root, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenArchiveAsync(string root, string hash, CancellationToken cancellationToken = default);
    Task<bool> ArchivedAsync(string root, string hash, CancellationToken cancellationToken = default);

    /// <summary>Every file under a root, excluding Gatherum's own bookkeeping. The scan
    /// the index is rebuilt from.</summary>
    IEnumerable<NodePath> Walk(string root);

    /// <summary>The root directories present on disk. A directory nobody has told
    /// Gatherum about still shows up here — that is the point.</summary>
    IEnumerable<string> Roots();

    /// <summary>Where a root's metadata and history live, for callers that need to read
    /// or write the sidecar directly.</summary>
    string SidecarDirectory(NodePath path);

    /// <summary>The instance's own files — a console's boot ROM, say — kept under the
    /// storage root's <c>.gatherum/system</c>, beside nobody's directory and outside
    /// every scan. <paramref name="relative"/> is <c>{console}/{name}</c>: a single
    /// directory and a filename, and nothing that could climb out of either.</summary>
    Task<StoredBlob> WriteSystemAsync(string relative, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenSystemAsync(string relative, CancellationToken cancellationToken = default);

    /// <summary>The hash and length of a system file, or null when there is none.</summary>
    Task<StoredBlob?> MeasureSystemAsync(string relative, CancellationToken cancellationToken = default);
    Task DeleteSystemAsync(string relative, CancellationToken cancellationToken = default);
}

public record StoredBlob(string Hash, long SizeBytes);
