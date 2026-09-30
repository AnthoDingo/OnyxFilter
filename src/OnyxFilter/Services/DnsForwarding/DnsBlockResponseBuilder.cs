using System;
using System.Net;
using System.Net.Sockets;
using OnyxFilter.Models.Settings;

namespace OnyxFilter.Services.DnsForwarding;

// Construit la réponse DNS envoyée pour un domaine bloqué par les listes de filtres (panneau
// "Configuration du serveur DNS" > "Mode de blocage" de /settings/dns).
public static class DnsBlockResponseBuilder
{
    // TTL des enregistrements A/AAAA synthétisés (modes "Adresse IP non spécifiée"/"personnalisée") :
    // volontairement court, pour qu'un changement de mode de blocage se propage vite aux clients.
    private const int BlockedRecordTtlSeconds = 60;

    public static byte[] Build(byte[] query, ushort queryType, DnsBlockingMode mode, string customBlockingIpv4, string customBlockingIpv6)
    {
        switch (mode)
        {
            case DnsBlockingMode.Refused:
                return BuildStatusResponse(query, rcode: 5); // REFUSED

            case DnsBlockingMode.NullIp:
                return BuildAddressResponseOrNxDomain(query, queryType, IPAddress.Any, IPAddress.IPv6Any);

            case DnsBlockingMode.CustomIp:
                IPAddress.TryParse(customBlockingIpv4, out IPAddress? customIpv4);
                IPAddress.TryParse(customBlockingIpv6, out IPAddress? customIpv6);
                return BuildAddressResponseOrNxDomain(query, queryType, customIpv4, customIpv6);

            case DnsBlockingMode.NxDomain:
            case DnsBlockingMode.Default:
            default:
                // "Par défaut" : NXDOMAIN, aussi bien pour les listes de filtres que pour la Sécurité de
                // navigation et le Contrôle parental, qui partagent tous ce même mode de blocage.
                return BuildStatusResponse(query, rcode: 3); // NXDOMAIN
        }
    }

    // Réponse sans enregistrement, ne modifiant que le code de statut (RCODE) de l'en-tête.
    private static byte[] BuildStatusResponse(byte[] query, int rcode)
    {
        byte[] response = (byte[])query.Clone();

        // Octet de poids fort des indicateurs : QR=1 (réponse), conserve OPCODE et RD, AA=TC=0.
        response[2] = (byte)(0x80 | (query[2] & 0x79));

        // Octet de poids faible des indicateurs : RA=1 (récursion disponible) + RCODE.
        response[3] = (byte)(0x80 | (rcode & 0x0F));

        return response;
    }

    // Réponse NOERROR avec un unique enregistrement A/AAAA pointant vers l'adresse fournie. Si le type
    // de question n'est ni A ni AAAA, ou si aucune adresse de la bonne famille n'est disponible (ex.
    // adresse IPv6 personnalisée non renseignée ou invalide), repli sur NXDOMAIN : impossible de
    // répondre une "adresse nulle" à une question qui n'en demande pas.
    private static byte[] BuildAddressResponseOrNxDomain(byte[] query, ushort queryType, IPAddress? ipv4Address, IPAddress? ipv6Address)
    {
        IPAddress? address = queryType switch
        {
            DnsMessageParser.TypeA => ipv4Address,
            DnsMessageParser.TypeAaaa => ipv6Address,
            _ => null,
        };

        if (address is null || !DnsMessageParser.TryGetQuestionEnd(query, out int questionEnd))
        {
            return BuildStatusResponse(query, rcode: 3); // NXDOMAIN
        }

        bool familyMatchesQueryType =
            (queryType == DnsMessageParser.TypeA && address.AddressFamily == AddressFamily.InterNetwork) ||
            (queryType == DnsMessageParser.TypeAaaa && address.AddressFamily == AddressFamily.InterNetworkV6);

        if (!familyMatchesQueryType)
        {
            // Protège contre une adresse IPv4 personnalisée invalide/mal renseignée (ex. une chaîne IPv6
            // saisie par erreur dans le champ IPv4), plutôt que de renvoyer une réponse incohérente.
            return BuildStatusResponse(query, rcode: 3); // NXDOMAIN
        }

        byte[] addressBytes = address.GetAddressBytes();

        byte[] head = new byte[questionEnd];
        Array.Copy(query, head, questionEnd);

        head[2] = (byte)(0x80 | (query[2] & 0x79)); // QR=1, conserve OPCODE et RD.
        head[3] = 0x80; // RA=1, RCODE=0 (NOERROR).
        head[6] = 0x00;
        head[7] = 0x01; // ANCOUNT = 1.

        int answerRecordLength = 2 + 2 + 2 + 4 + 2 + addressBytes.Length; // NAME(pointeur)+TYPE+CLASS+TTL+RDLENGTH+RDATA
        int tailLength = query.Length - questionEnd;

        byte[] result = new byte[head.Length + answerRecordLength + tailLength];
        System.Array.Copy(head, 0, result, 0, head.Length);

        int position = head.Length;
        result[position++] = 0xC0;
        result[position++] = 0x0C; // NAME : pointeur de compression vers la question (toujours à l'offset 12).
        result[position++] = (byte)(queryType >> 8);
        result[position++] = (byte)(queryType & 0xFF);
        result[position++] = 0x00;
        result[position++] = 0x01; // CLASS = IN.
        result[position++] = (byte)(BlockedRecordTtlSeconds >> 24);
        result[position++] = (byte)(BlockedRecordTtlSeconds >> 16);
        result[position++] = (byte)(BlockedRecordTtlSeconds >> 8);
        result[position++] = (byte)(BlockedRecordTtlSeconds & 0xFF);
        result[position++] = (byte)(addressBytes.Length >> 8);
        result[position++] = (byte)(addressBytes.Length & 0xFF);
        Array.Copy(addressBytes, 0, result, position, addressBytes.Length);
        position += addressBytes.Length;

        // Préserve les données suivant la question dans la requête d'origine (ex. l'enregistrement OPT
        // d'EDNS), qui restent valides en section Additional après notre unique enregistrement Answer.
        Array.Copy(query, questionEnd, result, position, tailLength);

        return result;
    }
}
