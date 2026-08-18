using Gatherum.Web.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Gatherum.Web.Components;

/// <summary>Base for components that act on behalf of the signed-in user.</summary>
public abstract class GatherumComponent : ComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationState { get; set; }

    protected Guid UserId { get; private set; }
    protected string UserName { get; private set; } = "";

    protected override async Task OnInitializedAsync()
    {
        var state = await (AuthenticationState
            ?? throw new InvalidOperationException("No authentication state available."));
        UserId = state.User.GetUserId();
        UserName = state.User.Identity?.Name ?? "";
    }
}
