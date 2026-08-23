using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

/// <summary>Which directory belongs to whom. Ownership is the path, so this is the whole
/// of the mapping in both directions: a node's owner picks the root its bytes go under,
/// and a reindex reads the root a file was found in to learn who owns it.</summary>
public class UserRoots(GatherumDbContext db)
{
    public async Task<string> ForAsync(Guid userId, CancellationToken ct = default) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.RootName).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"User {userId} has no root directory.");

    public async Task<Guid?> OwnerOfAsync(string rootName, CancellationToken ct = default)
    {
        var id = await db.Users.Where(u => u.RootName == rootName).Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
        return id;
    }

    /// <summary>A directory name for a new user, from the name their identity provider
    /// knows them by. Authelia's <c>preferred_username</c> is the login itself, so
    /// <c>sand_head</c> stays <c>sand_head</c> — the point of using it is that somebody
    /// looking at these directories with no Gatherum running recognises whose is whose,
    /// and mangling it into a slug would spend exactly the thing that makes it useful.
    ///
    /// Only what a directory genuinely cannot hold is replaced. Underscores, dots and
    /// hyphens survive; separators, control characters and the names Windows reserves do
    /// not.</summary>
    public static string Propose(string username, string subject, Guid id, Func<string, bool> taken)
    {
        foreach (var candidate in new[] { Sanitize(username), Sanitize(subject) })
        {
            if (candidate.Length == 0)
                continue;
            if (!taken(candidate))
                return candidate;
            for (var n = 2; n < 100; n++)
            {
                if (!taken($"{candidate}-{n}"))
                    return $"{candidate}-{n}";
            }
        }
        return $"user-{id:N}";
    }

    /// <summary>Names Windows refuses, kept out of the way even on Linux so a store stays
    /// portable. Gatherum's own <c>.gatherum</c> is not listed because it cannot be
    /// produced: a sanitized name never begins with a dot.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string username)
    {
        var kept = new string(username.Trim()
            .Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '-')
            .ToArray());
        while (kept.Contains("--"))
            kept = kept.Replace("--", "-");
        // A leading dot would make the directory hidden, and a trailing one is illegal on
        // Windows. Neither is worth keeping to preserve a name.
        kept = kept.Trim('.', '-');
        if (kept.Length > 60)
            kept = kept[..60].Trim('.', '-');
        return Reserved.Contains(kept) ? "" : kept;
    }
}
