using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OnyxFilter.Services.Filtering;

// Règles d'exception ("@@...") des listes de blocage abonnées, appliquées comme sur AdGuard Home : une
// exception de n'importe quelle liste lève le blocage venant de n'importe quelle autre liste. Formes
// prises en charge (motif appliqué au nom d'hôte demandé) :
//  - "@@||domaine.tld^"   : le domaine et ses sous-domaines (recherche par HashSet, cas le plus courant) ;
//  - "@@|domaine.tld^|"   : le domaine exact uniquement ;
//  - tout autre motif avec "*", "|" ou "^" (ex. "@@-ds.metric.gstatic.com^|", "@@||a*.b.com^|") :
//    converti en expression régulière (ces règles sont rares).
// Les règles avec modificateurs ("$...") et les expressions régulières ("@@/.../") sont ignorées :
// les appliquer partiellement pourrait débloquer plus que prévu.
public sealed class ListExceptionRules
{
    public static readonly ListExceptionRules Empty = new ListExceptionRules();

    private readonly HashSet<string> domainsAndSubdomains = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> exactDomains = new HashSet<string>(StringComparer.Ordinal);
    private readonly List<Regex> patterns = new List<Regex>();

    public int Count => domainsAndSubdomains.Count + exactDomains.Count + patterns.Count;

    public bool IsExcepted(string domain)
    {
        if (Count == 0)
        {
            return false;
        }

        string normalized = domain.TrimEnd('.').ToLowerInvariant();

        if (exactDomains.Contains(normalized) || DomainListRepository.ContainsDomainOrParent(normalized, domainsAndSubdomains))
        {
            return true;
        }

        foreach (Regex pattern in patterns)
        {
            if (pattern.IsMatch(normalized))
            {
                return true;
            }
        }

        return false;
    }

    // Retourne vrai si la ligne est une règle d'exception (prise en charge ou non) : elle ne doit alors
    // jamais être interprétée comme un domaine bloqué.
    public bool TryAdd(string line)
    {
        if (!line.StartsWith("@@", StringComparison.Ordinal))
        {
            return false;
        }

        string rule = line.Substring(2).ToLowerInvariant();

        if (rule.Length == 0 || rule[0] == '/' || rule.Contains('$', StringComparison.Ordinal))
        {
            return true;
        }

        bool subdomainAnchor = rule.StartsWith("||", StringComparison.Ordinal);
        bool startAnchor = !subdomainAnchor && rule.StartsWith('|');
        string body = rule.Substring(subdomainAnchor ? 2 : startAnchor ? 1 : 0);

        // "^" (séparateur) et "|" final : pour un nom d'hôte, tous deux équivalent à la fin du nom.
        bool endAnchor = false;

        while (body.EndsWith('|') || body.EndsWith('^'))
        {
            body = body.Substring(0, body.Length - 1);
            endAnchor = true;
        }

        if (body.Length == 0 || body.Contains('^', StringComparison.Ordinal) || body.Contains('|', StringComparison.Ordinal))
        {
            return true;
        }

        bool hasWildcard = body.Contains('*', StringComparison.Ordinal);

        if (!hasWildcard && endAnchor && subdomainAnchor && DomainListRepository.TryNormalizeDomain(body, out string domain))
        {
            domainsAndSubdomains.Add(domain);
            return true;
        }

        if (!hasWildcard && endAnchor && startAnchor && DomainListRepository.TryNormalizeDomain(body, out domain))
        {
            exactDomains.Add(domain);
            return true;
        }

        StringBuilder regex = new StringBuilder();
        regex.Append(subdomainAnchor ? @"^(?:.*\.)?" : startAnchor ? "^" : string.Empty);
        regex.Append(Regex.Escape(body).Replace(@"\*", ".*", StringComparison.Ordinal));
        regex.Append(endAnchor ? "$" : string.Empty);
        patterns.Add(new Regex(regex.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        return true;
    }
}
