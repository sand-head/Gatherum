namespace Gatherum.Infrastructure.Bookmarks;

/// <summary>The hosts a capture wants nothing from: ad networks, trackers, and the
/// consent machinery that exists to serve them. A bookmark is an archive of what a page
/// says, and none of that is what it says — so a rendered capture refuses these hosts
/// before their scripts can draw, and the snapshot strips what points at them so the
/// file carries no live wires back to the auction it was saved away from.
///
/// Instances of this are immutable sets of hosts; where a list *comes from* is
/// <see cref="AdBlocklistProvider"/>'s question — normally a community-maintained list
/// fetched just in time for a capture, with the small packaged list as the seed and the
/// fallback. An entry blocks its whole subtree — <c>doubleclick.net</c> is also
/// <c>ad.doubleclick.net</c> — and <see cref="Match"/> says which entry claimed a host,
/// so a capture can spare the one case where an "ad host" is the content: somebody
/// bookmarked the ad company.</summary>
public class AdBlocklist
{
    public static readonly AdBlocklist None = new([]);

    private readonly HashSet<string> hosts;

    /// <summary>One host per entry, taken as given (minus <c>#</c> comments). For text
    /// in the wild, use <see cref="Parse"/>, which knows the formats lists come in.</summary>
    public AdBlocklist(IEnumerable<string> lines) =>
        hosts = lines
            .Select(line => line.Split('#')[0].Trim().ToLowerInvariant())
            .Where(entry => entry.Length > 0)
            .ToHashSet();

    public bool IsEmpty => hosts.Count == 0;

    /// <summary>Reads the formats community lists actually come in: a hosts file
    /// (<c>0.0.0.0 host</c>), bare domains, wildcard domains (<c>*.host</c>), and
    /// Adblock-style host rules (<c>||host^</c>) — so the source URL can point at any
    /// of the usual lists. Anything fancier (exception rules, path filters, cosmetic
    /// selectors) is beyond a host blocker and skipped, as are the <c>localhost</c>
    /// entries hosts files carry, which is what requiring a dot quietly does.</summary>
    public static AdBlocklist Parse(IEnumerable<string> lines) =>
        new(lines.Select(ParseLine).Where(host => host.Contains('.')));

    private static string ParseLine(string line)
    {
        var hash = line.IndexOf('#');
        if (hash >= 0)
        {
            // A '#' mid-token is Adblock cosmetic syntax ("example.com##.ad"), and a
            // cosmetic rule read as a host would block the very site it decorates; only
            // a '#' opening the line or following whitespace is a comment.
            if (hash > 0 && !char.IsWhiteSpace(line[hash - 1]))
                return "";
            line = line[..hash];
        }
        var text = line.Trim();
        if (text.StartsWith('!') || text.StartsWith("@@", StringComparison.Ordinal))
            return "";
        if (text.StartsWith("||", StringComparison.Ordinal))
        {
            var end = text.IndexOfAny(['^', '$', '/']);
            text = end < 0 ? text[2..] : text[2..end];
        }
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        text = parts.Length switch
        {
            1 => parts[0],
            2 when parts[0] is "0.0.0.0" or "127.0.0.1" or "::1" or "::" => parts[1],
            _ => "",
        };
        if (text.StartsWith("*.", StringComparison.Ordinal))
            text = text[2..];
        return text.Contains('*') || text.Contains('/') ? "" : text;
    }

    /// <summary>The packaged list, embedded beside this class — the seed a fresh
    /// instance blocks with until the community list has been fetched, and the fallback
    /// when it cannot be.</summary>
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

    /// <summary>This list and another as one. The packaged registrable domains ride
    /// along under a fetched list of exact hosts, so an update never narrows what is
    /// blocked — and the broad entry is then what <see cref="Match"/> answers with,
    /// which is what keeps the first-party exemption whole when a community list names
    /// a site's subdomains one by one.</summary>
    public AdBlocklist Union(AdBlocklist other) => new(hosts.Concat(other.hosts));

    /// <summary>The most general entry a host falls under — a parent domain over the
    /// host itself — or null when the list has nothing to say about it.
    /// "notdoubleclick.net" matches nothing: containment is by label, never by
    /// substring. Most general on purpose: two hosts of one outfit answer with the same
    /// entry even when the list also names them exactly, and equal answers are how the
    /// first-party exemption recognizes a page's own things.</summary>
    public string? Match(string host)
    {
        if (hosts.Count == 0)
            return null;
        var labels = host.ToLowerInvariant().TrimEnd('.').Split('.');
        for (var i = labels.Length - 1; i >= 0; i--)
        {
            var suffix = string.Join('.', labels[i..]);
            if (hosts.Contains(suffix))
                return suffix;
        }
        return null;
    }

    public bool Blocks(Uri url) => Match(url.Host) is not null;
}
