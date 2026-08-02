using System;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Account.Shared;

public partial class RedirectToLogin : ComponentBase
{
    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    protected override void OnInitialized()
    {
        string returnUrl = Uri.EscapeDataString(NavigationManager.Uri);
        NavigationManager.NavigateTo($"login?returnUrl={returnUrl}", forceLoad: true);
    }
}
