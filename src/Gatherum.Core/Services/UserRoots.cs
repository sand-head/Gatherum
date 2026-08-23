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

    /// <summary>A directory name for a new user: their sign-in name where it can be one,
    /// falling back to their id. Deliberately readable — somebody will one day be looking
    /// at these directories in a file manager with no Gatherum running.</summary>
    public static string Propose(string displayName, string subject, Guid id,
        Func<string, bool> taken)
    {
        foreach (var candidate in new[] { Slug(displayName), Slug(subject) })
        {
            if (candidate.Length > 0 && !taken(candidate))
                return candidate;
        }
        var fallback = $"user-{id:N}"[..12];
        return taken(fallback) ? $"user-{id:N}" : fallback;
    }

    private static string Slug(string value)
    {
        var slug = new string(value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
