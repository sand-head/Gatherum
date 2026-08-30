using Gatherum.Core.Data;
using Gatherum.Core.Services;

namespace Gatherum.Tests;

[Collection("postgres")]
public class UserServiceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private GatherumDbContext db = null!;
    private UserService users = null!;

    public async Task InitializeAsync()
    {
        db = PostgresFixture.CreateContext(await postgres.CreateDatabaseAsync());
        users = new UserService(db, TimeProvider.System);
    }

    public async Task DisposeAsync() => await db.DisposeAsync();

    [Fact]
    public async Task With_no_admin_group_the_first_user_seen_is_the_admin()
    {
        var first = await users.GetOrCreateAsync("first", "first@example.org", "First", "first");
        var second = await users.GetOrCreateAsync("second", "second@example.org", "Second", "second");

        Assert.True(first.IsAdmin);
        Assert.False(second.IsAdmin);
    }

    [Fact]
    public async Task The_admin_group_decides_instead_of_arrival_order()
    {
        var first = await users.GetOrCreateAsync("first", "first@example.org", "First", "first",
            isAdmin: false);
        var second = await users.GetOrCreateAsync("second", "second@example.org", "Second", "second",
            isAdmin: true);

        Assert.False(first.IsAdmin);
        Assert.True(second.IsAdmin);
    }

    [Fact]
    public async Task Losing_the_admin_group_loses_admin_at_the_next_sign_in()
    {
        await users.GetOrCreateAsync("jess", "jess@example.org", "Jess", "jess", isAdmin: true);

        var demoted = await users.GetOrCreateAsync("jess", "jess@example.org", "Jess", "jess",
            isAdmin: false);

        Assert.False(demoted.IsAdmin);
    }

    [Fact]
    public async Task No_admin_group_configured_leaves_an_existing_admin_alone()
    {
        await users.GetOrCreateAsync("jess", "jess@example.org", "Jess", "jess", isAdmin: true);

        var again = await users.GetOrCreateAsync("jess", "jess@example.org", "Jess", "jess");

        Assert.True(again.IsAdmin);
    }

    [Fact]
    public async Task A_root_directory_survives_the_username_changing()
    {
        var created = await users.GetOrCreateAsync("jess", "jess@example.org", "Jess", "jess");
        var root = created.RootName;

        var renamed = await users.GetOrCreateAsync("jess", "jess@example.org", "Jess", "jessica");

        Assert.Equal("jessica", renamed.Username);
        Assert.Equal(root, renamed.RootName);
    }
}
