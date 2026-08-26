using Gatherum.Core;
using Gatherum.Core.Domain;

namespace Gatherum.Tests;

[Collection("postgres")]
public class ReadingPositionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ServiceHarness harness = null!;
    private Guid jess;
    private Guid sam;

    public async Task InitializeAsync()
    {
        harness = new ServiceHarness(await postgres.CreateDatabaseAsync());
        jess = await harness.AddUserAsync("jess");
        sam = await harness.AddUserAsync("sam");
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    [Fact]
    public async Task The_ribbon_moves_and_comes_back_where_it_was_left()
    {
        var book = await harness.Files.CreateTextNodeAsync(jess, null, "book");

        Assert.Null(await harness.Files.GetReadingPositionAsync(jess, book.Id));

        await harness.Files.SaveReadingPositionAsync(jess, book.Id, 2, 0.25);
        await harness.Files.SaveReadingPositionAsync(jess, book.Id, 3, 0.5);

        var position = await harness.Files.GetReadingPositionAsync(jess, book.Id);
        Assert.Equal(3, position!.Chapter);
        Assert.Equal(0.5, position.Progress);
    }

    [Fact]
    public async Task Each_reader_keeps_their_own_ribbon()
    {
        var book = await harness.Files.CreateTextNodeAsync(jess, null, "book");
        await harness.Access.GrantAsync(jess, book.Id, sam, AccessRole.Reader);

        await harness.Files.SaveReadingPositionAsync(jess, book.Id, 5, 0.9);
        await harness.Files.SaveReadingPositionAsync(sam, book.Id, 1, 0.1);

        Assert.Equal(5, (await harness.Files.GetReadingPositionAsync(jess, book.Id))!.Chapter);
        Assert.Equal(1, (await harness.Files.GetReadingPositionAsync(sam, book.Id))!.Chapter);
    }

    [Fact]
    public async Task A_node_the_reader_cannot_see_takes_no_ribbon()
    {
        var book = await harness.Files.CreateTextNodeAsync(jess, null, "private book");

        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Files.SaveReadingPositionAsync(sam, book.Id, 1, 0.5));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            harness.Files.GetReadingPositionAsync(sam, book.Id));

        // And a reader with no account has nothing to remember with — null, not a row.
        await harness.Access.SetAccessAsync(jess, book.Id, AccessMode.Public);
        Assert.Null(await harness.Files.GetReadingPositionAsync(null, book.Id));
    }

    [Fact]
    public async Task An_impossible_position_is_kept_as_the_nearest_possible_one()
    {
        var book = await harness.Files.CreateTextNodeAsync(jess, null, "book");

        await harness.Files.SaveReadingPositionAsync(jess, book.Id, -4, 1.7);

        var position = await harness.Files.GetReadingPositionAsync(jess, book.Id);
        Assert.Equal(0, position!.Chapter);
        Assert.Equal(1, position.Progress);
    }
}
