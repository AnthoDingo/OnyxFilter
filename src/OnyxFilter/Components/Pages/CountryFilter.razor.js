// Noms des pays (codes ISO 3166-1 alpha-2) dans la langue de la page, via Intl.DisplayNames.
export function getCountryNames(codes) {
    const names = {};
    const displayNames = new Intl.DisplayNames([document.documentElement.lang || "fr"], { type: "region" });

    for (const code of codes) {
        try {
            names[code] = displayNames.of(code) || code;
        } catch {
            names[code] = code;
        }
    }

    return names;
}
