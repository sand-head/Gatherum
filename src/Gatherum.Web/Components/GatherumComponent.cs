using Gatherum.Web.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Gatherum.Web.Components;

/// <summary>Base for components that act on behalf of whoever is looking — which, now
/// that a node can be public or unlisted, may be nobody at all.</summary>
public abstract class GatherumComponent : ComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    /// <summary>The viewer, or null for a visitor who has not signed in. Reads pass this
    /// straight down to the authorizer, which answers an anonymous caller with what is
    /// public and what they hold a link to.</summary>
    protected Guid? ViewerId { get; private set; }

    /// <summary>The viewer where one is required — anything that writes. Throws for an
    /// anonymous visitor, which is the correct answer to a write with nobody behind it.</summary>
    protected Guid UserId => ViewerId
        ?? throw new InvalidOperationException("This action needs a signed-in user.");

    protected bool SignedIn => ViewerId is not null;

    protected string UserName { get; private set; } = "";

    protected override async Task OnInitializedAsync()
    {
        var state = await (AuthenticationState
            ?? throw new InvalidOperationException("No authentication state available."));
        ViewerId = state.User.GetUserIdOrNull();
        UserName = state.User.Identity?.Name ?? "";
    }
}
