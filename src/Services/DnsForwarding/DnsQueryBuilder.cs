using System;
using System.Collections.Generic;
using System.Text;

namespace OnyxFilter.Services.DnsForwarding;

// Construit une requête DNS minimale (format "wire", RFC 1035) pour un nom et un type donnés.
// Utilisé uniquement pour les requêtes d'amorçage (résolution des noms d'hôte des serveurs en amont
// auprès des "Serveurs DNS d'amorçage").
public static class DnsQueryBuilder
{
    public static byte[] BuildQuery(string hostName, ushort queryType, out ushort transactionId)
    {
        transactionId = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);

        List<byte> buffer = new List<byte>(hostName.Length + 32)
        {
            (byte)(transactionId >> 8),
            (byte)(transactionId & 0xFF),
            0x01, // Flags : RD (recursion désirée) activé.
            0x00,
            0x00, 0x01, // QDCOUNT = 1
            0x00, 0x00, // ANCOUNT = 0
            0x00, 0x00, // NSCOUNT = 0
            0x00, 0x00, // ARCOUNT = 0
        };

        foreach (string label in hostName.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] labelBytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)labelBytes.Length);
            buffer.AddRange(labelBytes);
        }

        buffer.Add(0x00); // Fin du nom (label racine).

        buffer.Add((byte)(queryType >> 8));
        buffer.Add((byte)(queryType & 0xFF));
        buffer.Add(0x00);
        buffer.Add(0x01); // QCLASS = IN

        return buffer.ToArray();
    }
}
