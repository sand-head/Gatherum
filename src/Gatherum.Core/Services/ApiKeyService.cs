using System.Security.Cryptography;
using System.Text;
using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Gatherum.Core.Services;

public class ApiKeyService(GatherumDbContext db, TimeProvider clock)
{
    private const string TokenPrefix = "gk_";

    public async Task<CreatedApiKey> CreateAsync(Guid userId, string name, CancellationToken ct = default)
    {
        var token = TokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var key = new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            KeyHash = Hash(token),
            Prefix = token[..10],
            CreatedAt = clock.GetUtcNow(),
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new CreatedApiKey(key, token);
    }

    public async Task<ApiKey?> ValidateAsync(string token, CancellationToken ct = default)
    {
        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;
        var hash = Hash(token);
        var key = await db.ApiKeys.Include(k => k.User)
            .FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct);
        if (key is null)
            return null;
        key.LastUsedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return key;
    }

    public async Task RevokeAsync(Guid userId, Guid keyId, CancellationToken ct = default)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, ct)
            ?? throw new NotFoundException($"API key {keyId} not found.");
        key.RevokedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public Task<List<ApiKey>> ListAsync(Guid userId, CancellationToken ct = default) =>
        db.ApiKeys.Where(k => k.UserId == userId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(ct);

    internal static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public record CreatedApiKey(ApiKey Key, string PlaintextToken);
