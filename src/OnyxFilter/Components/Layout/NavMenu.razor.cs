using System;
using System.Globalization;
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

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    private string UserName { get; set; } = string.Empty;

    private string UserInitial => string.IsNullOrEmpty(UserName) ? "?" : UserName.Substring(0, 1).ToUpperInvariant();

    private static string AssemblyVersion => AppVersion.Display;

    private string LanguageUrl(string culture)
    {
        string current = "/" + Navigation.ToBaseRelativePath(Navigation.Uri);
        return $"culture/set?culture={culture}&redirectUri={Uri.EscapeDataString(current)}";
    }

    private static string IsCurrentLanguage(string culture)
    {
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == culture ? "true" : "false";
    }

    protected override async Task OnInitializedAsync()
    {
        UserName = L["Invité"];

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
