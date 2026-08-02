using System;
using System.Collections.Generic;
using System.Net;

namespace OnyxFilter.Services.DnsForwarding;

// Fait correspondre une adresse IP observée (statistiques, journal des requêtes) à un identifiant de
// client persistant (/settings/client) : adresse IP exacte ("192.168.1.42") ou plage CIDR
// ("192.168.1.0/24", "2001:db8::/32"). N'est appelé que depuis l'interface (résolution du nom/des
// réglages d'un client affiché), jamais sur le chemin de résolution DNS lui-même : contrairement à
// DnsAccessControl (qui a sa propre implémentation optimisée pour ce chemin chaud), la simplicité prime
// ici sur la performance.
public static class ClientIdentifierMatcher
{
    public static bool Matches(IPAddress candidate, string identifier)
    {
        string trimmed = identifier.Trim();

        if (trimmed.Length == 0)
        {
            return false;
        }

        int slashIndex = trimmed.IndexOf('/');

        if (slashIndex < 0)
        {
            return IPAddress.TryParse(trimmed, out IPAddress? exact) && exact.Equals(candidate);
        }

        string addressPart = trimmed.Substring(0, slashIndex);

        if (!IPAddress.TryParse(addressPart, out IPAddress? network)
            || !int.TryParse(trimmed.AsSpan(slashIndex + 1), out int prefixLength))
        {
            return false;
        }

        // Pas de gestion des adresses IPv4 mappées en IPv6 ("::ffff:a.b.c.d") : une plage CIDR IPv4 ne
        // correspondra donc qu'à une adresse candidate elle-même sous forme IPv4 simple, pas à sa
        // variante mappée. Simplification acceptable ici (interface uniquement, jamais le chemin de
        // résolution DNS).
        if (network.AddressFamily != candidate.AddressFamily)
        {
            return false;
        }

        byte[] networkBytes = network.GetAddressBytes();
        byte[] candidateBytes = candidate.GetAddressBytes();

        if (prefixLength < 0 || prefixLength > networkBytes.Length * 8)
        {
            return false;
        }

        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int index = 0; index < fullBytes; index++)
        {
            if (networkBytes[index] != candidateBytes[index])
            {
                return false;
            }
        }

        if (remainingBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));

            if ((networkBytes[fullBytes] & mask) != (candidateBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }

    public static bool MatchesAny(IPAddress candidate, IReadOnlyList<string> identifiers)
    {
        foreach (string identifier in identifiers)
        {
            if (Matches(candidate, identifier))
            {
                return true;
            }
        }

        return false;
    }
}
