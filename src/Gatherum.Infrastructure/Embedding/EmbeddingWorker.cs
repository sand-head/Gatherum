using Gatherum.Core;
using Gatherum.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure.Embedding;

/// <summary>Keeps the vectors caught up with the text. Unlike the media analyzer this
/// takes no hand-off from the request thread, because there is nothing an upload could
/// hand it that the database does not already say: a node is stale when its text
/// fingerprint and its embedded fingerprint differ, and that is true of a page somebody
/// edited, a file whose transcript just landed, and every node under a category that was
/// renamed — the last of which no upload path could have known to enqueue. One sweep,
/// one source of truth, nothing to forget.</summary>
public class EmbeddingWorker(
    IServiceScopeFactory scopes,
    IOptions<GatherumOptions> options,
    ILogger<EmbeddingWorker> logger) : BackgroundService
{
    /// <summary>Nodes per sweep. Small enough that a restart mid-backlog loses seconds,
    /// large enough that a rename of a busy category drains in a few passes.</summary>
    private const int BatchSize = 32;

    /// <summary>Nodes whose text the model has already refused once, and the fingerprint
    /// it refused. Kept in memory rather than on the row: a failure here is nearly always
    /// the endpoint being down or misconfigured, which is mended by fixing it and
    /// restarting, not by a column. An edit to the text clears it by changing the
    /// fingerprint.</summary>
    private readonly Dictionary<Guid, string> refused = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.Embedding;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, settings.SweepSeconds)));
        var announced = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!announced)
                    announced = await AnnounceBacklogAsync(stoppingToken);
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A sweep that throws must not end the worker, or every edit after it
                // goes unembedded with nothing in the log to say why.
                logger.LogError(ex, "An embedding sweep threw");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                return;
        }
    }

    /// <summary>What switching embeddings on looks like from the log: the size of the
    /// backlog, once, rather than a line per node.</summary>
    private async Task<bool> AnnounceBacklogAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var embeddings = scope.ServiceProvider.GetRequiredService<EmbeddingService>();
        var stale = await embeddings.StaleCountAsync(ct);
        if (stale > 0)
            logger.LogInformation("{Count} nodes waiting to be embedded", stale);
        return true;
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var embeddings = scope.ServiceProvider.GetRequiredService<EmbeddingService>();

        var stale = await embeddings.StaleNodesAsync(BatchSize, ct);
        foreach (var node in stale)
        {
            if (refused.TryGetValue(node.Id, out var fingerprint) && fingerprint == node.Fingerprint)
                continue;
            try
            {
                await embeddings.EmbedNodeAsync(node.Id, ct);
                refused.Remove(node.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not embed node {NodeId}", node.Id);
                refused[node.Id] = node.Fingerprint;
            }
        }
    }
}
