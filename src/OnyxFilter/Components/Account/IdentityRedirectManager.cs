using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Components;

namespace OnyxFilter.Components.Account;

public sealed class IdentityRedirectManager
{
    public const string StatusMessageKey = "Identity.StatusMessage";

    private readonly NavigationManager navigationManager;

    public IdentityRedirectManager(NavigationManager navigationManager)
    {
        this.navigationManager = navigationManager;
    }

    public void RedirectTo(string? uri)
    {
        uri ??= "";

        // Empêche les redirections ouvertes.
        if (!Uri.IsWellFormedUriString(uri, UriKind.Relative))
        {
            uri = navigationManager.ToBaseRelativePath(uri);
        }

        navigationManager.NavigateTo(uri);
    }

    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        string uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        string newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        RedirectTo(newUri);
    }

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    public void RedirectToCurrentPage()
    {
        RedirectTo(CurrentPath);
    }

    // .NET 10 n'a pas encore [SupplyParameterFromTempData] (ajouté en .NET 11) : on fait transiter
    // le message de statut via la query string plutôt que via TempData.
    public void RedirectToCurrentPage(Dictionary<string, object?> queryParameters)
    {
        RedirectTo(CurrentPath, queryParameters);
    }
}
