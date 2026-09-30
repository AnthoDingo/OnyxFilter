using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace OnyxFilter.Services.DnsForwarding;

// Analyse minimale du format "wire" DNS (RFC 1035), juste assez pour construire une clé de cache à
// partir de la question et déterminer la durée de vie (TTL) à partir de la réponse. Toute anomalie fait
// simplement échouer le parsing (la requête/réponse concernée n'est alors pas mise en cache), plutôt que
// de lever une exception.
public static class DnsMessageParser
{
    // Types d'enregistrement utilisés pour la résolution d'amorçage (bootstrap).
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    // Pseudo-enregistrement OPT (EDNS, RFC 6891) : son champ "TTL" transporte le RCODE étendu, la
    // version EDNS et le bit DO. Il ne doit jamais être réécrit comme un TTL ordinaire.
    public const ushort TypeOpt = 41;

    private const int HeaderLength = 12;
    private const int MaxLabelHops = 128;

    public static bool TryReadTransactionId(byte[] message, out ushort transactionId)
    {
        if (message.Length < 2)
        {
            transactionId = 0;
            return false;
        }

        transactionId = (ushort)((message[0] << 8) | message[1]);
        return true;
    }

    public static void WriteTransactionId(byte[] message, ushort transactionId)
    {
        message[0] = (byte)(transactionId >> 8);
        message[1] = (byte)(transactionId & 0xFF);
    }

    // Construit une clé de cache à partir de la question de la requête (nom + type + classe).
    // La section "Question" n'utilise jamais la compression de noms (RFC 1035 §4.1.4 : les pointeurs ne
    // peuvent référencer que des données précédentes dans le message, et la question est la toute
    // première section).
    public static bool TryBuildCacheKey(byte[] message, out string cacheKey)
    {
        cacheKey = string.Empty;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];

        if (questionCount < 1)
        {
            return false;
        }

        int offset = HeaderLength;

        if (!TryReadName(message, ref offset, allowCompression: false, out string name))
        {
            return false;
        }

        if (offset + 4 > message.Length)
        {
            return false;
        }

        int queryType = (message[offset] << 8) | message[offset + 1];
        int queryClass = (message[offset + 2] << 8) | message[offset + 3];

        cacheKey = name.ToLowerInvariant() + "|" + queryType + "|" + queryClass;
        return true;
    }

    // Extrait le nom de domaine de la question (en minuscules, sans point final), pour appliquer les
    // filtres d'accès. Même logique de lecture que TryBuildCacheKey.
    public static bool TryReadQuestionName(byte[] message, out string name)
    {
        name = string.Empty;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];

        if (questionCount < 1)
        {
            return false;
        }

        int offset = HeaderLength;

        if (!TryReadName(message, ref offset, allowCompression: false, out string rawName))
        {
            return false;
        }

        name = rawName.ToLowerInvariant();
        return true;
    }

    // Calcule l'offset situé juste après la première question du message (nom + QTYPE + QCLASS).
    // Utilisé pour insérer un enregistrement de réponse juste après la question, en conservant les
    // données suivantes (ex. l'enregistrement OPT d'EDNS) telles quelles. Ne gère correctement que le
    // cas usuel d'une seule question (QDCOUNT=1), comme le reste de cette classe.
    public static bool TryGetQuestionEnd(byte[] message, out int offset)
    {
        offset = HeaderLength;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];

        if (questionCount < 1)
        {
            return false;
        }

        int localOffset = HeaderLength;

        if (!TryReadName(message, ref localOffset, allowCompression: false, out _))
        {
            return false;
        }

        localOffset += 4; // QTYPE (2) + QCLASS (2)

        if (localOffset > message.Length)
        {
            return false;
        }

        offset = localOffset;
        return true;
    }

    // Extrait le type (QTYPE) de la question, pour appliquer "Désactiver la résolution IPv6" (blocage
    // des requêtes AAAA). Même logique de lecture que TryBuildCacheKey.
    public static bool TryReadQuestionType(byte[] message, out ushort queryType)
    {
        queryType = 0;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];

        if (questionCount < 1)
        {
            return false;
        }

        int offset = HeaderLength;

        if (!TryReadName(message, ref offset, allowCompression: false, out _))
        {
            return false;
        }

        if (offset + 4 > message.Length)
        {
            return false;
        }

        queryType = (ushort)((message[offset] << 8) | message[offset + 1]);
        return true;
    }

    // Construit une réponse vide (NOERROR, ANCOUNT=0) à partir d'une requête, pour "Désactiver la
    // résolution IPv6" : les requêtes AAAA sont interceptées avant tout envoi en amont et reçoivent
    // cette réponse, sans enregistrement.
    public static byte[] BuildEmptyResponse(byte[] query)
    {
        byte[] response = (byte[])query.Clone();

        // Octet de poids fort des indicateurs : QR=1 (réponse), conserve OPCODE et RD, AA=TC=0.
        response[2] = (byte)(0x80 | (query[2] & 0x79));

        // Octet de poids faible des indicateurs : RA=1 (récursion disponible), RCODE=0 (NOERROR).
        response[3] = 0x80;

        return response;
    }

    // Localise le pseudo-enregistrement OPT (EDNS, RFC 6891) dans la section Additional d'une requête,
    // pour y activer le bit DO (DNSSEC) ou y ajouter l'option EDNS Client Subnet. "rrOffset" pointe sur
    // le champ TYPE de l'enregistrement (après son nom) : TTL en rrOffset+4..+7, RDLENGTH en
    // rrOffset+8..+9, RDATA à partir de rrOffset+10.
    public static bool TryFindOptRecord(byte[] message, out int rrOffset, out int rdataLength)
    {
        rrOffset = -1;
        rdataLength = 0;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];
        int answerCount = (message[6] << 8) | message[7];
        int authorityCount = (message[8] << 8) | message[9];
        int additionalCount = (message[10] << 8) | message[11];

        int offset = HeaderLength;

        for (int i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: false, out _))
            {
                return false;
            }

            offset += 4; // QTYPE (2) + QCLASS (2)

            if (offset > message.Length)
            {
                return false;
            }
        }

        int recordsToSkip = answerCount + authorityCount;

        for (int i = 0; i < recordsToSkip; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: true, out _))
            {
                return false;
            }

            if (offset + 10 > message.Length)
            {
                return false;
            }

            int dataLength = (message[offset + 8] << 8) | message[offset + 9];
            offset += 10 + dataLength;

            if (offset > message.Length)
            {
                return false;
            }
        }

        for (int i = 0; i < additionalCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: true, out _))
            {
                return false;
            }

            if (offset + 10 > message.Length)
            {
                return false;
            }

            int recordType = (message[offset] << 8) | message[offset + 1];
            int dataLength = (message[offset + 8] << 8) | message[offset + 9];

            if (recordType == TypeOpt)
            {
                rrOffset = offset;
                rdataLength = dataLength;
                return true;
            }

            offset += 10 + dataLength;

            if (offset > message.Length)
            {
                return false;
            }
        }

        return false;
    }

    // Détermine si une réponse peut être mise en cache et, si oui, pendant combien de temps : le TTL
    // minimum parmi les enregistrements de la section "Answer". Une réponse en échec (RCODE != 0), sans
    // enregistrement, ou dont le TTL minimum vaut 0 (le serveur en amont demande explicitement de ne pas
    // la mettre en cache) n'est jamais mise en cache.
    public static bool TryGetCacheableTtl(byte[] message, out int ttlSeconds)
    {
        ttlSeconds = 0;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int flags = (message[2] << 8) | message[3];
        int responseCode = flags & 0x000F;

        if (responseCode != 0)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];
        int answerCount = (message[6] << 8) | message[7];

        if (answerCount < 1)
        {
            return false;
        }

        int offset = HeaderLength;

        for (int i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: false, out _))
            {
                return false;
            }

            offset += 4; // QTYPE (2) + QCLASS (2)

            if (offset > message.Length)
            {
                return false;
            }
        }

        int? minimumTtl = null;

        for (int i = 0; i < answerCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: true, out _))
            {
                return false;
            }

            if (offset + 10 > message.Length)
            {
                return false;
            }

            int recordTtl = (message[offset + 4] << 24) | (message[offset + 5] << 16) | (message[offset + 6] << 8) | message[offset + 7];
            int dataLength = (message[offset + 8] << 8) | message[offset + 9];

            offset += 10 + dataLength;

            if (offset > message.Length)
            {
                return false;
            }

            if (minimumTtl is null || recordTtl < minimumTtl)
            {
                minimumTtl = recordTtl;
            }
        }

        if (minimumTtl is null || minimumTtl.Value <= 0)
        {
            return false;
        }

        ttlSeconds = minimumTtl.Value;
        return true;
    }

    // Réécrit le TTL de tous les enregistrements des sections Answer, Authority et Additional d'une
    // réponse (sauf OPT/EDNS), en place. Utilisé par le cache DNS pour appliquer les bornes TTL
    // min/max et pour décrémenter le TTL des réponses servies depuis le cache. Retourne false (sans
    // modifier le message au-delà des enregistrements déjà traités) si le message est malformé.
    public static bool TryOverwriteTtls(byte[] message, int ttlSeconds)
    {
        if (message.Length < HeaderLength || ttlSeconds < 0)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];
        int answerCount = (message[6] << 8) | message[7];
        int authorityCount = (message[8] << 8) | message[9];
        int additionalCount = (message[10] << 8) | message[11];

        int offset = HeaderLength;

        for (int i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: false, out _))
            {
                return false;
            }

            offset += 4; // QTYPE (2) + QCLASS (2)

            if (offset > message.Length)
            {
                return false;
            }
        }

        int recordCount = answerCount + authorityCount + additionalCount;

        for (int i = 0; i < recordCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: true, out _))
            {
                return false;
            }

            if (offset + 10 > message.Length)
            {
                return false;
            }

            int recordType = (message[offset] << 8) | message[offset + 1];

            if (recordType != TypeOpt)
            {
                message[offset + 4] = (byte)(ttlSeconds >> 24);
                message[offset + 5] = (byte)(ttlSeconds >> 16);
                message[offset + 6] = (byte)(ttlSeconds >> 8);
                message[offset + 7] = (byte)(ttlSeconds & 0xFF);
            }

            int dataLength = (message[offset + 8] << 8) | message[offset + 9];
            offset += 10 + dataLength;

            if (offset > message.Length)
            {
                return false;
            }
        }

        return true;
    }

    // Utilisé pour la résolution d'amorçage (bootstrap) : extrait la première adresse IP (A ou AAAA,
    // selon "expectedType") trouvée dans la section "Answer" d'une réponse DNS, avec son TTL. Une
    // réponse en échec (RCODE != 0) ou sans enregistrement du type attendu ne retourne rien.
    public static bool TryExtractFirstAddress(byte[] message, ushort expectedType, out IPAddress? address, out int ttlSeconds)
    {
        address = null;
        ttlSeconds = 0;

        if (message.Length < HeaderLength)
        {
            return false;
        }

        int flags = (message[2] << 8) | message[3];
        int responseCode = flags & 0x000F;

        if (responseCode != 0)
        {
            return false;
        }

        int questionCount = (message[4] << 8) | message[5];
        int answerCount = (message[6] << 8) | message[7];

        if (answerCount < 1)
        {
            return false;
        }

        int offset = HeaderLength;

        for (int i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: false, out _))
            {
                return false;
            }

            offset += 4; // QTYPE (2) + QCLASS (2)

            if (offset > message.Length)
            {
                return false;
            }
        }

        for (int i = 0; i < answerCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: true, out _))
            {
                return false;
            }

            if (offset + 10 > message.Length)
            {
                return false;
            }

            int recordType = (message[offset] << 8) | message[offset + 1];
            int recordTtl = (message[offset + 4] << 24) | (message[offset + 5] << 16) | (message[offset + 6] << 8) | message[offset + 7];
            int dataLength = (message[offset + 8] << 8) | message[offset + 9];
            int dataOffset = offset + 10;

            if (dataOffset + dataLength > message.Length)
            {
                return false;
            }

            bool dataLengthMatchesExpectedType =
                (expectedType == TypeA && dataLength == 4) ||
                (expectedType == TypeAaaa && dataLength == 16);

            if (recordType == expectedType && dataLengthMatchesExpectedType)
            {
                byte[] addressBytes = new byte[dataLength];
                Array.Copy(message, dataOffset, addressBytes, 0, dataLength);
                address = new IPAddress(addressBytes);
                ttlSeconds = recordTtl;
                return true;
            }

            offset = dataOffset + dataLength;
        }

        return false;
    }

    // Type d'enregistrement TXT (RFC 1035 §3.3.14), utilisé par le service de sécurité de navigation
    // pour lire les hachages complets renvoyés par le service de recherche de préfixes.
    private const ushort TypeTxt = 16;

    // Extrait toutes les chaînes de caractères des enregistrements TXT de la section "Answer" d'une
    // réponse DNS. Une réponse en échec (RCODE != 0) ou malformée retourne simplement une liste vide
    // plutôt que de lever une exception (l'appelant traite alors la vérification comme "aucun résultat").
    public static List<string> ExtractTxtStrings(byte[] message)
    {
        List<string> result = new List<string>();

        if (message.Length < HeaderLength)
        {
            return result;
        }

        int flags = (message[2] << 8) | message[3];
        int responseCode = flags & 0x000F;

        if (responseCode != 0)
        {
            return result;
        }

        int questionCount = (message[4] << 8) | message[5];
        int answerCount = (message[6] << 8) | message[7];

        int offset = HeaderLength;

        for (int i = 0; i < questionCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: false, out _))
            {
                return result;
            }

            offset += 4; // QTYPE (2) + QCLASS (2)

            if (offset > message.Length)
            {
                return result;
            }
        }

        for (int i = 0; i < answerCount; i++)
        {
            if (!TryReadName(message, ref offset, allowCompression: true, out _))
            {
                return result;
            }

            if (offset + 10 > message.Length)
            {
                return result;
            }

            int recordType = (message[offset] << 8) | message[offset + 1];
            int dataLength = (message[offset + 8] << 8) | message[offset + 9];
            int dataOffset = offset + 10;

            if (dataOffset + dataLength > message.Length)
            {
                return result;
            }

            if (recordType == TypeTxt)
            {
                // RDATA TXT : une ou plusieurs "character-strings" (1 octet de longueur + données),
                // concaténées ici en une seule chaîne (le service interrogé n'en envoie qu'une par
                // enregistrement, mais le format en autorise plusieurs).
                int txtOffset = dataOffset;
                int txtEnd = dataOffset + dataLength;
                StringBuilder builder = new StringBuilder();

                while (txtOffset < txtEnd)
                {
                    int stringLength = message[txtOffset];
                    txtOffset += 1;

                    if (txtOffset + stringLength > txtEnd)
                    {
                        break;
                    }

                    builder.Append(Encoding.ASCII.GetString(message, txtOffset, stringLength));
                    txtOffset += stringLength;
                }

                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                }
            }

            offset = dataOffset + dataLength;
        }

        return result;
    }

    // Lit un nom de domaine encodé en labels à partir de "offset" (avancé jusqu'à la fin du nom dans le
    // flux principal, même en cas de compression). "allowCompression" doit être false pour la section
    // Question, qui n'utilise jamais de pointeurs.
    private static bool TryReadName(byte[] message, ref int offset, bool allowCompression, out string name)
    {
        StringBuilder builder = new StringBuilder();
        int currentOffset = offset;
        int? returnOffset = null;
        int hops = 0;

        while (true)
        {
            if (currentOffset >= message.Length)
            {
                name = string.Empty;
                return false;
            }

            int lengthByte = message[currentOffset];

            if (lengthByte == 0)
            {
                currentOffset += 1;
                break;
            }

            if ((lengthByte & 0xC0) == 0xC0)
            {
                if (!allowCompression || currentOffset + 1 >= message.Length)
                {
                    name = string.Empty;
                    return false;
                }

                int pointer = ((lengthByte & 0x3F) << 8) | message[currentOffset + 1];

                if (pointer >= currentOffset)
                {
                    // Un pointeur doit toujours référencer des données précédentes, pour éviter toute boucle.
                    name = string.Empty;
                    return false;
                }

                returnOffset ??= currentOffset + 2;
                currentOffset = pointer;
                hops += 1;

                if (hops > MaxLabelHops)
                {
                    name = string.Empty;
                    return false;
                }

                continue;
            }

            if (lengthByte > 63 || currentOffset + 1 + lengthByte > message.Length)
            {
                name = string.Empty;
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.ASCII.GetString(message, currentOffset + 1, lengthByte));
            currentOffset += 1 + lengthByte;
            hops += 1;

            if (hops > MaxLabelHops)
            {
                name = string.Empty;
                return false;
            }
        }

        offset = returnOffset ?? currentOffset;
        name = builder.Length == 0 ? "." : builder.ToString();
        return true;
    }
}
