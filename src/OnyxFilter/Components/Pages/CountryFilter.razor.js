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

// Drapeau emoji d'un code pays (indicateurs régionaux Unicode), comme ClientLocation.Flag côté serveur.
function flagOf(code) {
    return /^[A-Z]{2}$/.test(code)
        ? String.fromCodePoint(...[...code].map(c => 0x1F1E6 + c.charCodeAt(0) - 65))
        : "";
}

// Carte interactive : charge le SVG des pays (wwwroot/maps/world.svg, un <path data-country="FR"> par
// pays), affiche le drapeau, le nom et l'état du pays survolé, et transmet chaque clic à la page
// (dotNetRef.ToggleCountryFromMap). La couleur des pays est fixée par setMapState.
export async function initMap(container, dotNetRef, svgUrl, labels) {
    const response = await fetch(svgUrl);
    container.querySelector(".country-map-canvas").innerHTML = await response.text();

    const tooltip = container.querySelector(".country-map-tooltip");
    const displayNames = new Intl.DisplayNames([document.documentElement.lang || "fr"], { type: "region" });
    let hovered = null;

    const showTooltip = () => {
        const code = hovered.dataset.country;
        let name = code;
        try { name = displayNames.of(code) || code; } catch { }

        tooltip.querySelector(".client-flag").textContent = flagOf(code);
        tooltip.querySelector(".country-map-tooltip-name").textContent = name;
        tooltip.querySelector(".country-map-tooltip-state").textContent =
            hovered.classList.contains("is-blocked") ? labels.blocked : labels.allowed;
        tooltip.hidden = false;
    };

    container.addEventListener("mousemove", event => {
        hovered = event.target.closest("path[data-country]");

        if (!hovered) {
            tooltip.hidden = true;
            return;
        }

        showTooltip();
        const bounds = container.getBoundingClientRect();
        tooltip.style.left = `${event.clientX - bounds.left + 14}px`;
        tooltip.style.top = `${event.clientY - bounds.top + 14}px`;
    });

    container.addEventListener("mouseleave", () => {
        hovered = null;
        tooltip.hidden = true;
    });

    container.addEventListener("click", event => {
        const path = event.target.closest("path[data-country]");

        if (path) {
            dotNetRef.invokeMethodAsync("ToggleCountryFromMap", path.dataset.country);
        }
    });

    // Rafraîchit l'info-bulle quand l'état du pays survolé change (après un clic).
    container.refreshTooltip = () => {
        if (hovered && !tooltip.hidden) {
            showTooltip();
        }
    };
}

// Colore les pays : en mode "Allowlist", les pays sélectionnés sont autorisés et les autres bloqués ;
// en mode "Blocklist" (et en aperçu quand le filtrage est désactivé), l'inverse.
export function setMapState(container, mode, selectedCodes) {
    const selected = new Set(selectedCodes);
    const allowlist = mode === "Allowlist";

    container.classList.toggle("is-disabled", mode === "Disabled");

    for (const path of container.querySelectorAll("path[data-country]")) {
        const blocked = allowlist !== selected.has(path.dataset.country);
        path.classList.toggle("is-blocked", blocked);
        path.classList.toggle("is-allowed", !blocked);
    }

    container.refreshTooltip?.();
}
