#!/bin/bash
# Vérifie que chaque texte d'interface L["..."] (ou Html("...") pour un texte avec balisage) a sa traduction dans chaque
# SharedResource.<langue>.resx. Une clé manquante s'afficherait en français dans l'interface traduite.
# Usage : bash src/OnyxFilter/Resources/check-translations.sh [--warn]
#   --warn : n'échoue pas (code de sortie 0) et émet des annotations « ::warning:: » GitHub Actions (CI des PR).

set -euo pipefail

WARN=false
[[ "${1:-}" = "--warn" ]] && WARN=true

# Émet une annotation d'avertissement GitHub Actions en mode --warn.
annotate() {
    [[ "$WARN" = true ]] && echo "::warning file=src/OnyxFilter/Resources/$1::$2"
    return 0
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPONENTS="$(realpath "$SCRIPT_DIR/../Components")"

mapfile -t KEYS < <(
    grep -rhoP --include='*.razor' --include='*.razor.cs' \
        '(?:L\[|Html\()"\K[^"]*(?=")' "$COMPONENTS" |
    LC_ALL=C sort -u
)

FAILED=false

declare -A GROUP=() COUNT=()

for key in "${KEYS[@]}"; do
    lower="${key,,}"

    GROUP[$lower]+="${GROUP[$lower]:+ | }$key"
    COUNT[$lower]=$(( ${COUNT[$lower]:-0} + 1 ))
done

for lower in "${!COUNT[@]}"; do
    if [[ ${COUNT[$lower]} -gt 1 ]]; then
        FAILED=true

        echo "Clés identiques à la casse près : ${GROUP[$lower]}"
        annotate "SharedResource.resx" "Clés identiques à la casse près : ${GROUP[$lower]}"
    fi
done

for resx in "$SCRIPT_DIR"/SharedResource.*.resx; do
    # Noms des <data name="...">, entités XML décodées (&amp; en dernier), triés comme KEYS pour comm.
    mapfile -t MISSING < <(
        LC_ALL=C comm -23 \
            <(printf '%s\n' "${KEYS[@]}") \
            <(grep -oP '<data name="\K[^"]*' "$resx" |
                sed -e 's/&lt;/</g' -e 's/&gt;/>/g' -e 's/&quot;/"/g' -e "s/&apos;/'/g" -e 's/&amp;/\&/g' |
                LC_ALL=C sort -u)
    )

    if (( ${#MISSING[@]} > 0 )); then
        FAILED=true

        echo "${resx##*/} : ${#MISSING[@]} traduction(s) manquante(s)"
        printf '  %s\n' "${MISSING[@]}"

        for key in "${MISSING[@]}"; do
            annotate "${resx##*/}" "Traduction manquante : $key"
        done
    fi
done

if [[ "$FAILED" = true && "$WARN" = false ]]; then
  exit 1
fi

[[ "$FAILED" = true ]] && exit 0

echo "${#KEYS[@]} clés, toutes traduites."
