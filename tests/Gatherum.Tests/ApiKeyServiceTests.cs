using Gatherum.Core.Data;
using Gatherum.Core.Domain;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class ApiKeyServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private GatherumDbContext db = null!;
    private ApiKeyService keys = null!;
    private Guid jess;

    public async Task InitializeAsync()
    {
        db = PostgresFixture.CreateContext(await postgres.CreateDatabaseAsync());
        keys = new ApiKeyService(db, TimeProvider.System);
        var user = new User
        {
            Id = Guid.NewGuid(), Subject = "jess", Email = "jess@example.org", DisplayName = "jess",
            RootName = "jess",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        jess = user.Id;
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    [Fact]
    public async Task Only_the_hash_is_stored_and_the_token_validates()
    {
        var created = await keys.CreateAsync(jess, "laptop");

        Assert.StartsWith("gk_", created.PlaintextToken);
        Assert.DoesNotContain(created.PlaintextToken, created.Key.KeyHash);
        Assert.Equal(ApiKeyService.Hash(created.PlaintextToken), created.Key.KeyHash);

        var validated = await keys.ValidateAsync(created.PlaintextToken);
        Assert.NotNull(validated);
        Assert.Equal(jess, validated.UserId);
        Assert.NotNull(validated.LastUsedAt);
    }

    [Fact]
    public async Task Revoked_and_garbage_tokens_do_not_validate()
    {
        var created = await keys.CreateAsync(jess, "laptop");
        await keys.RevokeAsync(jess, created.Key.Id);

        Assert.Null(await keys.ValidateAsync(created.PlaintextToken));
        Assert.Null(await keys.ValidateAsync("gk_0000000000000000000000000000000000000000000000ff"));
        Assert.Null(await keys.ValidateAsync("not-even-a-key"));
    }

    [Fact]
    public async Task Users_cannot_revoke_each_others_keys()
    {
        var other = new User
        {
            Id = Guid.NewGuid(), Subject = "sam", Email = "sam@example.org", DisplayName = "sam",
            RootName = "sam",
        };
        db.Users.Add(other);
        await db.SaveChangesAsync();
        var created = await keys.CreateAsync(jess, "laptop");

        await Assert.ThrowsAsync<Gatherum.Core.NotFoundException>(
            () => keys.RevokeAsync(other.Id, created.Key.Id));
    }
}
