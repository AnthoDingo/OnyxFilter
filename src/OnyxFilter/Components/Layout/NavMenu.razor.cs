using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using OnyxFilter.Services.Updates;

namespace OnyxFilter.Components.Layout;

public partial class NavMenu : ComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState>? AuthenticationStateTask { get; set; }

    private string UserName { get; set; } = "Invité";

    private string UserInitial => string.IsNullOrEmpty(UserName) ? "?" : UserName.Substring(0, 1).ToUpperInvariant();

    private static string AssemblyVersion => AppVersion.Display;

    protected override async Task OnInitializedAsync()
    {
        if (AuthenticationStateTask is null)
        {
            return;
        }

        AuthenticationState state = await AuthenticationStateTask;
        ClaimsPrincipal user = state.User;

        if (user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name })
        {
            UserName = name;
        }
    }
}
