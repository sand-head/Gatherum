using Gatherum.Core.Services;

namespace Gatherum.Tests;

/// <summary>The username reaches the filesystem, so how it is spelled there matters more
/// than most string handling does.</summary>
public class UserRootTests
{
    private static bool Free(string _) => false;

    [Fact]
    public void An_authelia_username_becomes_a_directory_of_the_same_name()
    {
        // The whole reason for using the username: somebody looking at these directories
        // with no Gatherum running recognises whose is whose.
        Assert.Equal("sand_head", UserRoots.Propose("sand_head", "sub", Guid.NewGuid(), Free));
        Assert.Equal("jess", UserRoots.Propose("jess", "sub", Guid.NewGuid(), Free));
        Assert.Equal("a.b-c_d", UserRoots.Propose("a.b-c_d", "sub", Guid.NewGuid(), Free));
    }

    [Fact]
    public void Only_what_a_directory_cannot_hold_is_replaced()
    {
        Assert.Equal("sand-head", UserRoots.Sanitize("sand/head"));
        Assert.Equal("sand-head", UserRoots.Sanitize("sand head"));
        Assert.Equal("user", UserRoots.Sanitize("  user  "));
        // Leading dots hide a directory; trailing dots are illegal on Windows.
        Assert.Equal("hidden", UserRoots.Sanitize(".hidden."));
        Assert.Equal("", UserRoots.Sanitize(".."));
        Assert.Equal("", UserRoots.Sanitize("CON"));
    }

    [Fact]
    public void A_sanitized_name_can_never_collide_with_gatherums_own_directory()
    {
        // Not a special case in the code — it falls out of never returning a name that
        // starts with a dot, which is the property worth holding.
        foreach (var attempt in new[] { ".gatherum", "..gatherum", "-.gatherum", ".GATHERUM" })
        {
            var sanitized = UserRoots.Sanitize(attempt);
            Assert.NotEqual(".gatherum", sanitized, StringComparer.OrdinalIgnoreCase);
            Assert.False(sanitized.StartsWith('.'));
        }
    }

    [Fact]
    public void A_username_that_cannot_be_a_directory_falls_back_rather_than_colliding()
    {
        var id = Guid.NewGuid();
        Assert.Equal("subject-name", UserRoots.Propose("///", "subject name", id, Free));
        Assert.Equal($"user-{id:N}", UserRoots.Propose("///", "...", id, Free));
    }

    [Fact]
    public void A_taken_name_is_suffixed_rather_than_shared()
    {
        // Two people whose usernames sanitize to the same thing must not share a
        // directory: ownership is the path, so that would be one owning the other's files.
        Assert.Equal("sand-head-2",
            UserRoots.Propose("sand head", "sub", Guid.NewGuid(), name => name == "sand-head"));
    }
}
