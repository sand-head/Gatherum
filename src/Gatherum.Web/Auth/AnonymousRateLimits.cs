using System.Threading.RateLimiting;
using Gatherum.Core;
using Gatherum.Web.Auth;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Gatherum.Web.Auth;

/// <summary>A budget for callers with no session. Public nodes put Gatherum on the
/// internet, and the internet is not two people: the read surface needs a ceiling, and
/// the search surface needs a much lower one because its semantic half runs a model on
/// the request path.
///
/// Signed-in callers are never limited. Gatherum is a knowledge base for two people who
/// authenticated to get here; metering them would only make their own instance worse, and
/// an IP-keyed bucket shared with the internet would do it unpredictably.</summary>
public static class AnonymousRateLimits
{
    public const string Read = "public-read";
    public const string Search = "public-search";

    public static IServiceCollection AddAnonymousRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many requests." }, token);
            };

            limiter.AddPolicy(Read, http => Partition(http,
                o => o.Sharing.AnonymousReadsPerMinute));
            limiter.AddPolicy(Search, http => Partition(http,
                o => o.Sharing.AnonymousSearchesPerMinute));
        });
        return services;
    }

    private static RateLimitPartition<string> Partition(HttpContext http,
        Func<GatherumOptions, int> budget)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<GatherumOptions>>().Value;
        if (http.User.GetUserIdOrNull() is { } userId)
            return RateLimitPartition.GetNoLimiter($"user:{userId}");

        // Everything without a session shares a bucket per client address. Behind a
        // reverse proxy this is only as good as the forwarded-headers configuration, which
        // is the operator's to get right — and the failure mode is one bucket for
        // everybody, which is the safe direction.
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, budget(options)),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = Math.Max(0, options.Sharing.AnonymousQueueDepth),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }
}
