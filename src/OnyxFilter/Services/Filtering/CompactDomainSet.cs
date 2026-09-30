using System;
using System.Collections.Generic;
using System.Text;

namespace OnyxFilter.Services.Filtering;

// Ensemble de domaines en lecture seule, optimisé pour la mémoire plutôt que pour la vitesse
// d'écriture : construit une seule fois (à partir du HashSet<string> temporaire utilisé par
// DomainListRepository pour fusionner les listes abonnées), puis conservé tel quel jusqu'au
// prochain rafraîchissement.
//
// Un HashSet<string> classique coûte environ 90-110 octets par domaine (en-tête d'objet string +
// tableau de caractères UTF-16 + entrée de table de hachage), et génère autant d'objets pour le
// ramasse-miettes. Pour les grandes listes de blocage abonnées (plusieurs millions de domaines),
// ceci représente plusieurs centaines de mégaoctets. Ici, tous les domaines sont encodés en UTF-8
// et concaténés dans un unique tampon contigu (un seul objet), indexés par une table de hachage à
// adressage ouvert qui ne stocke que des entiers : le coût tombe à environ 30-40 octets par
// domaine, sans million de petits objets à parcourir pour le GC.
public sealed class CompactDomainSet
{
    public static readonly CompactDomainSet Empty = new CompactDomainSet(Array.Empty<string>());

    // Table de hachage à adressage ouvert (sondage linéaire) : buckets[i] vaut 0 si vide, sinon
    // (index dans domainOffsets/domainLengths) + 1.
    private readonly byte[] domainBytes;
    private readonly int[] domainOffsets;
    private readonly int[] domainLengths;
    private readonly int[] buckets;
    private readonly int bucketMask;

    public int Count { get; }

    public CompactDomainSet(IReadOnlyCollection<string> domains)
    {
        Count = domains.Count;
        domainOffsets = new int[Count];
        domainLengths = new int[Count];

        int totalBytes = 0;

        foreach (string domain in domains)
        {
            totalBytes += Encoding.UTF8.GetByteCount(domain);
        }

        domainBytes = new byte[totalBytes];

        // Facteur de charge ~0.75 : bon compromis entre mémoire (table plus petite) et nombre de
        // sondages lors des recherches, pour une structure qui n'est jamais redimensionnée.
        int bucketCount = 4;
        long minimumBucketCount = (long)Count * 4 / 3 + 1;

        while (bucketCount < minimumBucketCount)
        {
            bucketCount <<= 1;
        }

        bucketMask = bucketCount - 1;
        buckets = new int[bucketCount];

        int writeOffset = 0;
        int entryIndex = 0;

        foreach (string domain in domains)
        {
            int byteCount = Encoding.UTF8.GetBytes(domain, 0, domain.Length, domainBytes, writeOffset);

            domainOffsets[entryIndex] = writeOffset;
            domainLengths[entryIndex] = byteCount;

            ulong hash = ComputeHash(domainBytes.AsSpan(writeOffset, byteCount));
            InsertIntoBuckets(hash, entryIndex);

            writeOffset += byteCount;
            entryIndex++;
        }
    }

    private void InsertIntoBuckets(ulong hash, int entryIndex)
    {
        int bucket = (int)(hash & (ulong)bucketMask);

        while (buckets[bucket] != 0)
        {
            bucket = (bucket + 1) & bucketMask;
        }

        buckets[bucket] = entryIndex + 1;
    }

    // Recherche exacte (pas de remontée aux domaines parents : voir DomainListRepository.ContainsDomainOrParent
    // pour ce comportement, utilisé sur le chemin de résolution DNS).
    public bool Contains(ReadOnlySpan<char> domain)
    {
        if (Count == 0)
        {
            return false;
        }

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(domain.Length);
        Span<byte> encoded = maxByteCount <= 512 ? stackalloc byte[maxByteCount] : new byte[maxByteCount];
        int byteCount = Encoding.UTF8.GetBytes(domain, encoded);
        ReadOnlySpan<byte> needle = encoded.Slice(0, byteCount);

        ulong hash = ComputeHash(needle);
        int bucket = (int)(hash & (ulong)bucketMask);

        while (true)
        {
            int slot = buckets[bucket];

            if (slot == 0)
            {
                return false;
            }

            int entryIndex = slot - 1;

            if (domainLengths[entryIndex] == byteCount
                && domainBytes.AsSpan(domainOffsets[entryIndex], byteCount).SequenceEqual(needle))
            {
                return true;
            }

            bucket = (bucket + 1) & bucketMask;
        }
    }

    // FNV-1a 64 bits : simple, rapide, distribution suffisante pour une table de hachage interne
    // (pas d'exigence cryptographique ici, contrairement à GetCacheFilePath dans DomainListRepository).
    private static ulong ComputeHash(ReadOnlySpan<byte> data)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte value in data)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
