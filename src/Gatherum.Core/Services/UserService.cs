using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

public class UserService(GatherumDbContext db, TimeProvider clock)
{
    /// <summary>Called on every OIDC sign-in.</summary>
    /// <param name="isAdmin">What the identity provider's admin group says, or null when no
    /// admin group is configured and the question is not the provider's to answer. When it
    /// answers, it answers every time: losing the group loses admin at the next sign-in,
    /// which is the only way a claim read per request can be authoritative. When it does
    /// not, admin stays where it was — with the first account ever seen.</param>
    public async Task<User> GetOrCreateAsync(string subject, string email, string displayName,
        string username, bool? isAdmin = null, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, ct);
        if (user is not null)
        {
            if (user.Email != email || user.DisplayName != displayName
                || user.Username != username || isAdmin is { } admin && user.IsAdmin != admin)
            {
                user.Email = email;
                user.DisplayName = displayName;
                // Deliberately not touching RootName: their directory keeps the name it
                // was given. Renaming it would mean moving every file they own, and
                // nothing good comes of a directory that moved on its own.
                user.Username = username;
                if (isAdmin is { } value)
                    user.IsAdmin = value;
                await db.SaveChangesAsync(ct);
            }
            return user;
        }

        var id = Guid.NewGuid();
        var takenRoots = await db.Users.Select(u => u.RootName).ToListAsync(ct);
        user = new User
        {
            Id = id,
            Subject = subject,
            Email = email,
            DisplayName = displayName,
            Username = username,
            RootName = UserRoots.Propose(username, subject, id,
                name => takenRoots.Contains(name, StringComparer.OrdinalIgnoreCase)),
            IsAdmin = isAdmin ?? !await db.Users.AnyAsync(ct),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public Task<User?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<List<User>> ListAsync(CancellationToken ct = default) =>
        db.Users.OrderBy(u => u.CreatedAt).ToListAsync(ct);
}
