using System.Security.Claims;
using Gatherum.Web.Auth;

namespace Gatherum.Tests;

public class OidcGroupsTests
{
    private static ClaimsPrincipal WithGroups(params string[] groups) =>
        new(new ClaimsIdentity(groups.Select(g => new Claim("groups", g))));

    [Fact]
    public void Reads_every_group_claim()
    {
        var groups = OidcGroups.From(WithGroups("gatherum", "admins"), "groups");

        Assert.Equal(["gatherum", "admins"], groups);
    }

    [Fact]
    public void Reads_the_configured_claim_and_no_other()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("roles", "gatherum"), new Claim("groups", "readers")]));

        Assert.Equal(["gatherum"], OidcGroups.From(principal, "roles"));
        Assert.Equal(["readers"], OidcGroups.From(principal, "groups"));
    }

    [Fact]
    public void A_token_carrying_no_groups_is_a_member_of_nothing()
    {
        var groups = OidcGroups.From(new ClaimsPrincipal(new ClaimsIdentity()), "groups");

        Assert.Empty(groups);
        Assert.False(OidcGroups.IsMember(groups, "gatherum"));
    }

    [Fact]
    public void Membership_ignores_case_because_the_name_is_typed_by_hand()
    {
        var groups = OidcGroups.From(WithGroups("Gatherum"), "groups");

        Assert.True(OidcGroups.IsMember(groups, "gatherum"));
        Assert.True(OidcGroups.IsMember(groups, "GATHERUM"));
    }

    [Fact]
    public void A_group_that_only_looks_similar_is_not_a_match()
    {
        var groups = OidcGroups.From(WithGroups("gatherum-readers"), "groups");

        Assert.False(OidcGroups.IsMember(groups, "gatherum"));
    }
}
