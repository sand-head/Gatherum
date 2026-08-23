using System.Text.Json;
using System.Text.Json.Serialization;
using Gatherum.Core.Abstractions;

namespace Gatherum.Infrastructure.Storage;

/// <summary>The sidecar, as one indented JSON file per directory: readable by a person
/// with a text editor, which is the only interoperability guarantee that survives losing
/// everything else.</summary>
public class JsonNodeMetadataStore(IFileStorage storage) : INodeMetadataStore
{
    private const string FileName = "meta.json";

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One writer per directory. Two nodes in the same folder saving at once
    /// would otherwise read-modify-write over each other's entry.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<NodeMetadata?> ReadAsync(NodePath path, CancellationToken ct = default)
    {
        var entries = await ReadDirectoryAsync(path, ct);
        return entries.GetValueOrDefault(path.Name);
    }

    public async Task WriteAsync(NodePath path, NodeMetadata metadata, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var entries = await ReadDirectoryAsync(path, ct);
            entries[path.Name] = metadata;
            await SaveAsync(path, entries, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RemoveAsync(NodePath path, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var entries = await ReadDirectoryAsync(path, ct);
            if (entries.Remove(path.Name))
                await SaveAsync(path, entries, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Dictionary<string, NodeMetadata>> ReadDirectoryAsync(NodePath path,
        CancellationToken ct)
    {
        var file = Path.Combine(storage.SidecarDirectory(path), FileName);
        if (!File.Exists(file))
            return new Dictionary<string, NodeMetadata>(StringComparer.Ordinal);
        try
        {
            await using var stream = File.OpenRead(file);
            var entries = await JsonSerializer.DeserializeAsync<
                Dictionary<string, NodeMetadata>>(stream, Format, ct);
            return entries is null
                ? new Dictionary<string, NodeMetadata>(StringComparer.Ordinal)
                : new Dictionary<string, NodeMetadata>(entries, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A hand-edited sidecar that no longer parses must not stop the file it
            // describes from being indexed: the bytes are the system of record, and this
            // is commentary on them.
            return new Dictionary<string, NodeMetadata>(StringComparer.Ordinal);
        }
    }

    private async Task SaveAsync(NodePath path, Dictionary<string, NodeMetadata> entries,
        CancellationToken ct)
    {
        var directory = storage.SidecarDirectory(path);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, FileName);
        if (entries.Count == 0)
        {
            File.Delete(file);
            return;
        }

        var temp = file + $".incoming-{Guid.NewGuid():N}";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, entries, Format, ct);
        }
        File.Move(temp, file, overwrite: true);
    }
}
