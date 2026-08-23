using System.Security.Claims;
using Gatherum.Core.Domain;

namespace Gatherum.Web.Auth;

public static class GatherumClaims
{
    public const string UserId = "gatherum:user_id";
    public const string Admin = "gatherum:admin";

    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(UserId), out var id)
            ? id
            : throw new InvalidOperationException("The current principal has no Gatherum user id.");

    /// <summary>The signed-in user, or null for an anonymous caller. Anonymous is a real
    /// state now that a node can be public, and it flows all the way down to
    /// <c>INodeAuthorizer.VisibleTo</c>, which answers it with public nodes and nothing
    /// else. Endpoints that can serve the internet ask for this; everything else asks for
    /// <see cref="GetUserId"/> and throws if there is nobody there.</summary>
    public static Guid? GetUserIdOrNull(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(UserId), out var id) ? id : null;

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.HasClaim(Admin, "true");

    public static ClaimsIdentity ToIdentity(this User user, string authenticationType)
    {
        var identity = new ClaimsIdentity(authenticationType, ClaimTypes.Name, roleType: null);
        identity.AddClaim(new Claim(UserId, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        if (user.IsAdmin)
            identity.AddClaim(new Claim(Admin, "true"));
        return identity;
    }
}
