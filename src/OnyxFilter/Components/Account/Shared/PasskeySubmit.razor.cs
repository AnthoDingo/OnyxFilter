using System.Collections.Generic;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;

namespace OnyxFilter.Components.Account.Shared;

public partial class PasskeySubmit : ComponentBase
{
    [Inject]
    public IAntiforgery Antiforgery { get; set; } = default!;

    // Rendu statique côté serveur (pages de connexion et « Mon compte ») : la requête HTTP est disponible.
    [CascadingParameter]
    public HttpContext? HttpContext { get; set; }

    // Jeton antiforgery transmis au script, qui l'envoie en en-tête aux endpoints des options de passkey
    // (/Account/PasskeyCreationOptions et /Account/PasskeyRequestOptions exigent un jeton valide).
    private AntiforgeryTokenSet? Tokens { get; set; }

    [Parameter]
    [EditorRequired]
    public PasskeyOperation Operation { get; set; }

    [Parameter]
    [EditorRequired]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public string? EmailName { get; set; }

    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    [Parameter(CaptureUnmatchedValues = true)]
    public IDictionary<string, object>? AdditionalAttributes { get; set; }

    protected override void OnInitialized()
    {
        Tokens = HttpContext is null ? null : Antiforgery.GetAndStoreTokens(HttpContext);
    }
}
