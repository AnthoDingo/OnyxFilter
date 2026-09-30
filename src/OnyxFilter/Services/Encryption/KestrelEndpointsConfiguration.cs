using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace OnyxFilter.Services.Encryption;

// Point d'écoute HTTPS de Kestrel, sous forme de configuration « Kestrel:Endpoints » que Kestrel surveille :
// l'ajouter ou le retirer ouvre ou ferme le port HTTPS sans redémarrage (voir HttpsEndpointService). Vide au
// démarrage : Kestrel ouvre alors normalement les adresses HTTP configurées (ASPNETCORE_URLS, --urls), et ses
// rechargements ne touchent ensuite qu'aux points d'écoute déclarés ici.
public sealed class KestrelEndpointsConfigurationSource : IConfigurationSource
{
    public KestrelEndpointsConfigurationProvider Provider { get; } = new KestrelEndpointsConfigurationProvider();

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        return Provider;
    }
}

public sealed class KestrelEndpointsConfigurationProvider : ConfigurationProvider
{
    private const string HttpsEndpointKey = "Kestrel:Endpoints:OnyxHttps:Url";

    public string? HttpsUrl => Data.TryGetValue(HttpsEndpointKey, out string? url) ? url : null;

    // null ferme le port HTTPS. Kestrel applique le changement de lui-même (jeton de rechargement).
    public void SetHttpsUrl(string? url)
    {
        if (string.Equals(url, HttpsUrl, StringComparison.Ordinal))
        {
            return;
        }

        // Nouveau dictionnaire plutôt que modification en place : des lectures peuvent être en cours.
        Dictionary<string, string?> data = new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);

        if (url is null)
        {
            data.Remove(HttpsEndpointKey);
        }
        else
        {
            data[HttpsEndpointKey] = url;
        }

        Data = data;
        OnReload();
    }
}
