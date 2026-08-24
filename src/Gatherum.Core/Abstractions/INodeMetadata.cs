using Gatherum.Core.Domain;

namespace Gatherum.Core.Abstractions;

/// <summary>What a path cannot say about a file. Everything here is carried on disk
/// beside the bytes it describes, so losing the database costs recomputation and nothing
/// else. Absent entirely, a file is still a node: filename for a title, sniffed media
/// type, private to whoever owns the directory. That degradation is the design.
///
/// People are named by their root directory rather than by their id, because an id is a
/// database's opinion and a directory is a fact on disk. A grant recorded as a Guid would
/// be meaningless the moment the database it came from was gone — which is the one
/// scenario this file exists for.</summary>
public record NodeMetadata
{
    public Guid Id { get; init; }

    /// <summary>Overrides the filename-derived title, and null when nothing overrides it —
    /// which is the common case and why a bare directory reads correctly.</summary>
    public string? Title { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Whether this node is a category rather than a page about one. Nothing
    /// about a Markdown file's bytes says it is a subject, so this is the one thing about
    /// a category the sidecar has to carry.</summary>
    public bool Category { get; init; }

    /// <summary>The categories this node is filed under, by <em>name</em> — the same
    /// choice, and for the same reason, as recording a grant by root directory rather
    /// than by user id: an id is a database's opinion and this file exists for the day
    /// there is no database. On a category page these are the categories it is nested
    /// under, which is the only place nesting is written down.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];
    public AccessMode Access { get; init; } = AccessMode.Private;
    public bool Inherit { get; init; } = true;

    /// <summary>Who this is shared with, by root directory name.</summary>
    public IReadOnlyList<MetadataGrant> Grants { get; init; } = [];

    public IReadOnlyList<MetadataVersion> History { get; init; } = [];
}

public record MetadataGrant(string Root, AccessRole Role);

/// <summary>One superseded version, enough to rebuild its row. The hash addresses the
/// archive beside it.</summary>
public record MetadataVersion(
    int Number,
    string Hash,
    string FileName,
    string MediaType,
    long SizeBytes,
    string UploadedByRoot,
    DateTimeOffset UploadedAt);

/// <summary>Reads and writes the per-directory sidecar. One file per directory, keyed by
/// the filename within it, so a directory carries its own metadata wherever it is moved
/// or copied and there is no central registry to keep in step.</summary>
public interface INodeMetadataStore
{
    Task<NodeMetadata?> ReadAsync(NodePath path, CancellationToken cancellationToken = default);
    Task WriteAsync(NodePath path, NodeMetadata metadata, CancellationToken cancellationToken = default);
    Task RemoveAsync(NodePath path, CancellationToken cancellationToken = default);
}
