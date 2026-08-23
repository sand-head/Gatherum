using Gatherum.Core;
using Gatherum.Core.Abstractions;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gatherum.Infrastructure.Analysis;

/// <summary>Runs the analyzers off the request thread, one file at a time. Sequential on
/// purpose: the model behind this is a single local runner, and three transcripts racing
/// each other through it finish no sooner than three in a row while making every one of
/// them time out.</summary>
public class MediaAnalysisWorker(
    MediaAnalysisQueue queue,
    IServiceScopeFactory scopes,
    IOptions<GatherumOptions> options,
    ILogger<MediaAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepAsync(stoppingToken);
        await foreach (var versionId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await AnalyzeAsync(versionId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unanalyzable file must not take the worker down with it, or every
                // upload after it silently stops being described.
                logger.LogError(ex, "Analysis of version {VersionId} threw", versionId);
            }
        }
    }

    /// <summary>What the queue cannot remember across a restart: work that was still
    /// Pending when the process died, and — the first time an endpoint is configured —
    /// the media that was uploaded back when nothing could read it. Uploads survive a
    /// restart because the bytes are written before anything else; this is what makes
    /// their transcripts survive one too.</summary>
    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var files = scope.ServiceProvider.GetRequiredService<FileService>();

            if (options.Value.Analysis.BackfillExisting)
            {
                var backfilled = await files.BackfillAnalysisAsync(ct);
                if (backfilled.Count > 0)
                    logger.LogInformation(
                        "Queueing {Count} files uploaded before analysis was configured",
                        backfilled.Count);
            }

            var pending = await files.PendingAnalysisIdsAsync(ct);
            if (pending.Count == 0)
                return;
            logger.LogInformation("{Count} media files waiting to be analyzed", pending.Count);
            foreach (var id in pending)
                queue.Enqueue(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not sweep for unanalyzed media");
        }
    }

    private async Task AnalyzeAsync(Guid versionId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider;
        var db = provider.GetRequiredService<GatherumDbContext>();
        var files = provider.GetRequiredService<FileService>();

        var version = await db.FileVersions
            .Where(v => v.Id == versionId && v.Analysis == MediaAnalysisState.Pending)
            .Select(v => new { v.Hash, v.MediaType, v.FileName, v.SizeBytes })
            .FirstOrDefaultAsync(ct);
        if (version is null)
            return;

        var analyzer = provider.GetServices<IMediaAnalyzer>()
            .FirstOrDefault(a => a.CanAnalyze(version.MediaType, version.FileName));
        if (analyzer is null)
        {
            await files.FailAnalysisAsync(versionId,
                $"No analyzer claims {version.MediaType}.", ct);
            return;
        }

        var source = new MediaSource(version.Hash, version.MediaType, version.FileName,
            version.SizeBytes, token => files.OpenVersionAsync(versionId, token));

        try
        {
            logger.LogInformation("Analyzing {FileName} ({MediaType}, {Bytes} bytes)",
                version.FileName, version.MediaType, version.SizeBytes);
            var analysis = await analyzer.AnalyzeAsync(source, ct);
            await files.ApplyAnalysisAsync(versionId, analysis, ct);
            logger.LogInformation("Analyzed {FileName}: {Transcript} chars transcribed, " +
                "{Summary} chars summarized", version.FileName,
                analysis.Transcript.Length, analysis.Summary.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not analyze {FileName}", version.FileName);
            await files.FailAnalysisAsync(versionId, ex.Message, ct);
        }
    }
}
