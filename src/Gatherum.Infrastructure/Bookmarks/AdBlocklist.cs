namespace Gatherum.Infrastructure.Bookmarks;

/// <summary>The hosts a capture wants nothing from: ad networks, trackers, and the
/// consent machinery that exists to serve them. A bookmark is an archive of what a page
/// says, and none of that is what it says — so a rendered capture refuses these hosts
/// before their scripts can draw, and the snapshot strips what points at them so the
/// file carries no live wires back to the auction it was saved away from.
///
/// The list ships embedded, a curated set of the networks that matter rather than a
/// live filter list: nothing in Gatherum fetches the web unasked, and a blocklist
/// fetched on a schedule would be the first exception. An entry blocks its whole
/// subtree — <c>doubleclick.net</c> is also <c>ad.doubleclick.net</c> — and
/// <see cref="Match"/> says which entry claimed a host, so a capture can spare the one
/// case where an "ad host" is the content: somebody bookmarked the ad company.</summary>
public class AdBlocklist
{
    public static readonly AdBlocklist None = new([]);

    private readonly HashSet<string> hosts;

    /// <summary>One entry per line; blank lines and <c>#</c> comments pass through.</summary>
    public AdBlocklist(IEnumerable<string> lines) =>
        hosts = lines
            .Select(line => line.Split('#')[0].Trim().ToLowerInvariant())
            .Where(entry => entry.Length > 0)
            .ToHashSet();

    public bool IsEmpty => hosts.Count == 0;

    /// <summary>The packaged list, embedded beside this class.</summary>
    public static AdBlocklist Packaged()
    {
        using var stream = typeof(AdBlocklist).Assembly.GetManifestResourceStream(
                "Gatherum.Infrastructure.Bookmarks.AdHosts.txt")
            ?? throw new InvalidOperationException("The packaged ad host list is missing.");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return new AdBlocklist(lines);
    }

    /// <summary>The entry a host falls under — itself or a parent domain — or null when
    /// the list has nothing to say about it. "notdoubleclick.net" matches nothing:
    /// containment is by label, never by substring.</summary>
    public string? Match(string host)
    {
        if (hosts.Count == 0)
            return null;
        var candidate = host.ToLowerInvariant().TrimEnd('.');
        while (candidate.Length > 0)
        {
            if (hosts.Contains(candidate))
                return candidate;
            var dot = candidate.IndexOf('.');
            if (dot < 0)
                return null;
            candidate = candidate[(dot + 1)..];
        }
        return null;
    }

    public bool Blocks(Uri url) => Match(url.Host) is not null;
}
