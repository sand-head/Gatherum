using System.Threading.Channels;

namespace Gatherum.Core.Services;

/// <summary>The hand-off from an upload to the background analyzer: the request thread
/// drops a version id and returns to the user, the worker picks it up. Unbounded
/// because the backlog is already bounded by how fast two people can upload, and
/// because dropping an id would strand a Pending row — the worker's startup sweep is
/// what makes a lost id, or a restart mid-transcript, recoverable anyway.</summary>
public class MediaAnalysisQueue
{
    private readonly Channel<Guid> versions =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(Guid versionId) => versions.Writer.TryWrite(versionId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        versions.Reader.ReadAllAsync(cancellationToken);
}
