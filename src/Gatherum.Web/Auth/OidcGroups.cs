using System.Security.Claims;

namespace Gatherum.Web.Auth;

/// <summary>What the identity provider's group claim says about a sign-in, and nothing
/// more. A group is the provider's idea: Gatherum reads the claim as each person arrives
/// and keeps no notion of a group afterward — there is nothing here to grant to, join,
/// leave, or administer, and sharing still names people. That is the whole point of doing
/// it this way rather than modelling groups: take somebody out of the group in Authelia
/// and their next sign-in has already lost whatever it conferred.
///
/// The comparison is case-insensitive because a group name is typed into configuration by
/// hand and provider consoles disagree about capitalizing it.</summary>
public static class OidcGroups
{
    public static IReadOnlyList<string> From(ClaimsPrincipal principal, string claimType) =>
        claimType.Length == 0 ? [] : principal.FindAll(claimType).Select(c => c.Value).ToList();

    public static bool IsMember(IReadOnlyList<string> groups, string group) =>
        groups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
}
