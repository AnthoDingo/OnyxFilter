using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace OnyxFilter.Services.DnsForwarding;

// Construit une réponse DNS à deux enregistrements pour les redirections de type CNAME (utilisé par
// "Utiliser la Recherche Sécurisée", pour les moteurs redirigés via CNAME chez AdGuard Home : Google,
// Bing, DuckDuckGo, Ecosia, YouTube, Pixabay). Reproduit ce que ferait un résolveur récursif normal en
// suivant une chaîne CNAME : un premier enregistrement CNAME (nom demandé -> cible de la redirection),
// suivi de l'enregistrement A/AAAA déjà résolu pour cette cible, pour que le client obtienne une réponse
// complète en une seule requête (la plupart des résolveurs "stub" du système d'exploitation ne
// redemandent pas eux-mêmes la cible d'un CNAME auprès de leur résolveur configuré).
public static class DnsRewriteResponseBuilder
{
    // TTL de l'enregistrement CNAME synthétisé : volontairement court, pour qu'un changement de
    // configuration de la Recherche Sécurisée se propage vite aux clients (même choix que
    // DnsBlockResponseBuilder pour ses propres enregistrements synthétisés).
    private const int CnameTtlSeconds = 60;

    public static byte[] BuildCnameChain(byte[] query, ushort queryType, string cnameTarget, IPAddress resolvedAddress, int addressTtlSeconds)
    {
        if (!DnsMessageParser.TryGetQuestionEnd(query, out int questionEnd))
        {
            return DnsMessageParser.BuildEmptyResponse(query);
        }

        byte[] head = new byte[questionEnd];
        Array.Copy(query, head, questionEnd);

        head[2] = (byte)(0x80 | (query[2] & 0x79)); // QR=1, conserve OPCODE et RD.
        head[3] = 0x80; // RA=1, RCODE=0 (NOERROR).
        head[6] = 0x00;
        head[7] = 0x02; // ANCOUNT = 2 (CNAME + adresse résolue).

        byte[] targetNameBytes = EncodeName(cnameTarget);
        byte[] addressBytes = resolvedAddress.GetAddressBytes();

        const int fixedRecordHeaderLength = 2 + 2 + 2 + 4 + 2; // NAME(pointeur)+TYPE+CLASS+TTL+RDLENGTH
        int cnameRecordLength = fixedRecordHeaderLength + targetNameBytes.Length;
        int addressRecordLength = fixedRecordHeaderLength + addressBytes.Length;

        // Offset, dans le message final, du nom encodé en toutes lettres dans le RDATA de
        // l'enregistrement CNAME : c'est sa première (et unique) apparition en clair, donc la seule
        // référence valide pour le pointeur de compression de l'enregistrement d'adresse ci-dessous
        // (RFC 1035 §4.1.4 : un pointeur ne peut référencer que des données précédentes dans le message).
        int targetNameOffset = head.Length + fixedRecordHeaderLength;

        int tailLength = query.Length - questionEnd;

        byte[] result = new byte[head.Length + cnameRecordLength + addressRecordLength + tailLength];
        Array.Copy(head, 0, result, 0, head.Length);

        int position = head.Length;

        // Enregistrement 1 : CNAME du nom demandé vers la cible de la redirection.
        result[position++] = 0xC0;
        result[position++] = 0x0C; // NAME : pointeur de compression vers la question (toujours à l'offset 12).
        result[position++] = 0x00;
        result[position++] = 0x05; // TYPE = 5 (CNAME).
        result[position++] = 0x00;
        result[position++] = 0x01; // CLASS = IN.
        result[position++] = (byte)(CnameTtlSeconds >> 24);
        result[position++] = (byte)(CnameTtlSeconds >> 16);
        result[position++] = (byte)(CnameTtlSeconds >> 8);
        result[position++] = (byte)(CnameTtlSeconds & 0xFF);
        result[position++] = (byte)(targetNameBytes.Length >> 8);
        result[position++] = (byte)(targetNameBytes.Length & 0xFF);
        Array.Copy(targetNameBytes, 0, result, position, targetNameBytes.Length);
        position += targetNameBytes.Length;

        // Enregistrement 2 : adresse déjà résolue pour la cible, avec un pointeur de compression vers son
        // nom écrit juste au-dessus plutôt que de le répéter en toutes lettres.
        int clampedTtl = Math.Max(1, addressTtlSeconds);
        ushort pointerValue = (ushort)targetNameOffset;

        result[position++] = (byte)(0xC0 | (pointerValue >> 8));
        result[position++] = (byte)(pointerValue & 0xFF);
        result[position++] = (byte)(queryType >> 8);
        result[position++] = (byte)(queryType & 0xFF);
        result[position++] = 0x00;
        result[position++] = 0x01; // CLASS = IN.
        result[position++] = (byte)(clampedTtl >> 24);
        result[position++] = (byte)(clampedTtl >> 16);
        result[position++] = (byte)(clampedTtl >> 8);
        result[position++] = (byte)(clampedTtl & 0xFF);
        result[position++] = (byte)(addressBytes.Length >> 8);
        result[position++] = (byte)(addressBytes.Length & 0xFF);
        Array.Copy(addressBytes, 0, result, position, addressBytes.Length);
        position += addressBytes.Length;

        // Préserve les données suivant la question dans la requête d'origine (ex. l'enregistrement OPT
        // d'EDNS), comme DnsBlockResponseBuilder.
        Array.Copy(query, questionEnd, result, position, tailLength);

        return result;
    }

    private static byte[] EncodeName(string name)
    {
        List<byte> buffer = new List<byte>(name.Length + 2);

        foreach (string label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)labelBytes.Length);
            buffer.AddRange(labelBytes);
        }

        buffer.Add(0x00); // Fin du nom (label racine).

        return buffer.ToArray();
    }
}
