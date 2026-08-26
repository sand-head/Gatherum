using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace Gatherum.Infrastructure.Bookmarks;

/// <summary>Where a capture's blocklist comes from: a community-maintained list,
/// fetched just in time. The fetch happens inside a capture somebody asked for — never
/// on a schedule, keeping the rule that nothing here touches the web unasked — and the
/// result is held for a day, so it costs the first capture a moment and the rest of the
/// day's captures nothing. Until the list has been had, and whenever it cannot be, the
/// packaged list stands in: a blocklist that cannot be fetched must degrade to blocking
/// less, never to failing the capture that wanted it.</summary>
public class AdBlocklistProvider
{
    public const string ClientName = "adhosts";

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>How long a failed fetch keeps its silence — retrying every capture
    /// would turn a dead list host into a toll on each bookmark.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    private readonly string url;
    private readonly IHttpClientFactory? clients;
    private readonly TimeProvider clock;
    private readonly ILogger<AdBlocklistProvider>? logger;
    private readonly SemaphoreSlim refreshing = new(1, 1);
    private readonly AdBlocklist packaged = AdBlocklist.None;
    private volatile AdBlocklist current;
    private DateTimeOffset staleAt = DateTimeOffset.MinValue;

    public AdBlocklistProvider(string url, IHttpClientFactory clients, TimeProvider clock,
        ILogger<AdBlocklistProvider> logger)
    {
        this.url = url;
        this.clients = clients;
        this.clock = clock;
        this.logger = logger;
        current = packaged = AdBlocklist.Packaged();
    }

    /// <summary>A provider that only ever answers with this list — blocking switched
    /// off, no URL configured, or a test that wants no clock and no wire.</summary>
    public AdBlocklistProvider(AdBlocklist only)
    {
        url = "";
        clock = TimeProvider.System;
        current = only;
    }

    /// <summary>The list to block this capture with: the community list when it is
    /// fresh or freshly fetched, the last good one while a refresh fails, the packaged
    /// one before any fetch has succeeded.</summary>
    public async ValueTask<AdBlocklist> CurrentAsync(CancellationToken ct = default)
    {
        if (url.Length == 0 || clock.GetUtcNow() < staleAt)
            return current;
        await refreshing.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() >= staleAt)
                await RefreshAsync(ct);
        }
        finally
        {
            refreshing.Release();
        }
        return current;
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            using var client = clients!.CreateClient(ClientName);
            var body = await client.GetStringAsync(url, ct);
            var fetched = AdBlocklist.Parse(body.Split('\n'));
            if (fetched.IsEmpty)
                throw new InvalidOperationException("the list parsed to nothing");
            current = fetched.Union(packaged);
            staleAt = clock.GetUtcNow() + MaxAge;
            logger?.LogInformation("Ad blocklist refreshed from {Url}.", url);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The capture itself is over; its budget is not this fetch's to spend.
            throw;
        }
        catch (Exception ex)
        {
            staleAt = clock.GetUtcNow() + RetryDelay;
            logger?.LogWarning("Could not refresh the ad blocklist from {Url} ({Reason}); "
                + "blocking with the last list until the next try.", url, ex.Message);
        }
    }
}
