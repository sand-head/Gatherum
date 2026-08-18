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
