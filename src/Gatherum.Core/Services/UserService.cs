using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

public class UserService(GatherumDbContext db, TimeProvider clock)
{
    /// <summary>Called on every OIDC sign-in. The first user ever seen becomes admin.</summary>
    public async Task<User> GetOrCreateAsync(string subject, string email, string displayName,
        CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Subject == subject, ct);
        if (user is not null)
        {
            if (user.Email != email || user.DisplayName != displayName)
            {
                user.Email = email;
                user.DisplayName = displayName;
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
            RootName = UserRoots.Propose(displayName, subject, id, takenRoots.Contains),
            IsAdmin = !await db.Users.AnyAsync(ct),
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
