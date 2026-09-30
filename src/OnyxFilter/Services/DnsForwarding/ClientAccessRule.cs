using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OnyxFilter.Services.DnsForwarding;

// Règle d'accès client : une adresse IP ou un sous-réseau CIDR (IPv4 ou IPv6), tel que saisi dans les listes
// « clients autorisés » et « clients refusés » des paramètres DNS. Précompilée en préfixe de 128 bits (IPv4
// mappé en "::ffff:a.b.c.d", pour comparer uniformément avec les clients reçus via une socket dual-stack) :
// la comparaison avec l'adresse d'un client ne fait aucune allocation.
public readonly struct ClientAccessRule
{
    private readonly AddressBits network;

    // Longueur du préfixe sur 128 bits (96 + n pour un sous-réseau IPv4 /n).
    private readonly int prefixLength;

    private ClientAccessRule(AddressBits network, int prefixLength, bool isIpv4, string source)
    {
        this.network = network;
        this.prefixLength = prefixLength;
        IsIpv4 = isIpv4;
        Source = source;
    }

    public bool IsIpv4 { get; }

    // Entrée d'origine, telle qu'enregistrée dans les paramètres (sans les espaces autour).
    public string Source { get; }

    public bool IsSingleAddress => prefixLength == 128;

    // Analyse une entrée des paramètres : "192.168.1.50", "192.168.1.0/24", "2001:db8::/32"…
    public static bool TryParse(string? entry, out ClientAccessRule rule)
    {
        rule = default;

        if (entry is null)
        {
            return false;
        }

        string trimmed = entry.Trim();
        string addressPart = trimmed;
        int prefix = -1;
        int slashIndex = trimmed.IndexOf('/');

        if (slashIndex >= 0)
        {
            addressPart = trimmed.Substring(0, slashIndex);

            if (!int.TryParse(trimmed.AsSpan(slashIndex + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out prefix) || prefix < 0)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(addressPart, out IPAddress? address))
        {
            return false;
        }

        bool isIpv4 = address.AddressFamily == AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6;
        int maxPrefixLength = isIpv4 ? 32 : 128;

        if (prefix < 0)
        {
            prefix = maxPrefixLength;
        }
        else if (prefix > maxPrefixLength)
        {
            return false;
        }

        int totalPrefixLength = isIpv4 ? 96 + prefix : prefix;
        rule = new ClientAccessRule(AddressBits.FromIpAddress(address).Mask(totalPrefixLength), totalPrefixLength, isIpv4, trimmed);
        return true;
    }

    // Variante stricte pour les saisies de l'API : refuse en plus les formes IPv4 abrégées ("10.1" ou "10",
    // acceptées par IPAddress.TryParse et presque toujours des fautes de frappe) et les zones IPv6 ("%eth0").
    public static bool TryParseStrict(string? entry, out ClientAccessRule rule)
    {
        rule = default;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        string trimmed = entry.Trim();
        int slashIndex = trimmed.IndexOf('/');
        string addressPart = slashIndex >= 0 ? trimmed.Substring(0, slashIndex) : trimmed;

        if (addressPart.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        if (!addressPart.Contains(':', StringComparison.Ordinal) && addressPart.Split('.').Length != 4)
        {
            return false;
        }

        if (slashIndex >= 0 && !IsDigits(trimmed.AsSpan(slashIndex + 1)))
        {
            return false;
        }

        return TryParse(trimmed, out rule);
    }

    public bool Matches(IPAddress address)
    {
        return Matches(AddressBits.FromIpAddress(address));
    }

    internal bool Matches(AddressBits address)
    {
        return address.Mask(prefixLength).Equals(network);
    }

    // Vrai si toutes les adresses de other sont couvertes par cette règle.
    public bool Contains(ClientAccessRule other)
    {
        return other.prefixLength >= prefixLength && other.network.Mask(prefixLength).Equals(network);
    }

    // Deux préfixes se chevauchent si et seulement si l'un contient l'autre.
    public bool Overlaps(ClientAccessRule other)
    {
        return Contains(other) || other.Contains(this);
    }

    // Forme canonique : "192.168.1.50", "192.168.1.0/24", "2001:db8::/32".
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[16];
        network.WriteTo(bytes);

        if (IsIpv4)
        {
            IPAddress address = new IPAddress(bytes.Slice(12, 4));
            return prefixLength == 128 ? address.ToString() : $"{address}/{prefixLength - 96}";
        }

        IPAddress ipv6 = new IPAddress(bytes);
        return prefixLength == 128 ? ipv6.ToString() : $"{ipv6}/{prefixLength}";
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || text.Length > 3)
        {
            return false;
        }

        foreach (char character in text)
        {
            if (character < '0' || character > '9')
            {
                return false;
            }
        }

        return true;
    }
}

// Adresse IP sous forme de 128 bits (IPv4 mappé en "::ffff:a.b.c.d"), sans allocation sur le tas.
internal readonly struct AddressBits : IEquatable<AddressBits>
{
    public readonly ulong High;
    public readonly ulong Low;

    public AddressBits(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    public static AddressBits FromIpAddress(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes.Clear();
            bytes[10] = 0xFF;
            bytes[11] = 0xFF;
            address.TryWriteBytes(bytes.Slice(12), out int _);
        }
        else
        {
            address.TryWriteBytes(bytes, out int _);
        }

        return new AddressBits(ReadUInt64BigEndian(bytes, 0), ReadUInt64BigEndian(bytes, 8));
    }

    public AddressBits Mask(int prefixLength)
    {
        ulong high = High;
        ulong low = Low;

        if (prefixLength <= 0)
        {
            high = 0;
            low = 0;
        }
        else if (prefixLength < 64)
        {
            high &= ulong.MaxValue << (64 - prefixLength);
            low = 0;
        }
        else if (prefixLength == 64)
        {
            // Cas à part : "ulong.MaxValue << 64" ne vaut pas 0 en C# (le décalage est pris modulo 64).
            low = 0;
        }
        else if (prefixLength < 128)
        {
            low &= ulong.MaxValue << (128 - prefixLength);
        }

        return new AddressBits(high, low);
    }

    public void WriteTo(Span<byte> bytes)
    {
        for (int index = 0; index < 8; index++)
        {
            bytes[index] = (byte)(High >> (56 - (8 * index)));
            bytes[8 + index] = (byte)(Low >> (56 - (8 * index)));
        }
    }

    public bool Equals(AddressBits other)
    {
        return High == other.High && Low == other.Low;
    }

    public override bool Equals(object? obj)
    {
        return obj is AddressBits other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(High, Low);
    }

    private static ulong ReadUInt64BigEndian(Span<byte> bytes, int offset)
    {
        ulong value = 0;

        for (int index = 0; index < 8; index++)
        {
            value = (value << 8) | bytes[offset + index];
        }

        return value;
    }
}
