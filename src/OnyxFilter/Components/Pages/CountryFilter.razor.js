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

    const zoom = initZoom(container);

    container.addEventListener("click", event => {
        // Un glissement (déplacement ou pincement de la carte) ne compte pas comme un clic.
        if (zoom?.consumeDrag()) {
            return;
        }

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

const MAX_ZOOM = 16;
const DRAG_THRESHOLD = 5;

// Zoom de la carte, en réduisant le viewBox du SVG (les frontières restent nettes) : molette de la
// souris et pincement à deux doigts centrés sur le pointeur, glisser pour se déplacer une fois zoomé,
// et barre de zoom (.country-map-zoom) en bas au centre de la carte.
function initZoom(container) {
    const svg = container.querySelector(".country-map-canvas svg");
    const bar = container.querySelector(".country-map-zoom");

    if (!svg || !bar) {
        return null;
    }

    const [baseX, baseY, baseWidth, baseHeight] = (svg.getAttribute("viewBox") || "0 0 1000 435").split(/[\s,]+/).map(Number);
    const range = bar.querySelector(".country-map-zoom-range");
    const zoomIn = bar.querySelector("[data-zoom=in]");
    const zoomOut = bar.querySelector("[data-zoom=out]");
    const reset = bar.querySelector("[data-zoom=reset]");
    const view = { x: baseX, y: baseY, scale: 1 };

    const width = () => baseWidth / view.scale;
    const height = () => baseHeight / view.scale;

    const apply = () => {
        // La vue reste dans les limites de la carte.
        view.x = Math.min(Math.max(view.x, baseX), baseX + baseWidth - width());
        view.y = Math.min(Math.max(view.y, baseY), baseY + baseHeight - height());
        svg.setAttribute("viewBox", `${view.x} ${view.y} ${width()} ${height()}`);

        // Curseur logarithmique : chaque cran agrandit d'autant, quel que soit le niveau.
        range.value = Math.round(Math.log(view.scale) / Math.log(MAX_ZOOM) * 100);
        zoomOut.disabled = reset.disabled = view.scale <= 1;
        zoomIn.disabled = view.scale >= MAX_ZOOM;
        container.classList.toggle("is-zoomed", view.scale > 1);
    };

    // Zoome vers `scale` en gardant fixe le point de la carte situé sous (clientX, clientY).
    const zoomAt = (scale, clientX, clientY) => {
        const bounds = svg.getBoundingClientRect();
        const ratioX = bounds.width ? (clientX - bounds.left) / bounds.width : 0.5;
        const ratioY = bounds.height ? (clientY - bounds.top) / bounds.height : 0.5;
        const pointX = view.x + ratioX * width();
        const pointY = view.y + ratioY * height();

        view.scale = Math.min(Math.max(scale, 1), MAX_ZOOM);
        view.x = pointX - ratioX * width();
        view.y = pointY - ratioY * height();
        apply();
    };

    const zoomAtCenter = scale => {
        const bounds = svg.getBoundingClientRect();
        zoomAt(scale, bounds.left + bounds.width / 2, bounds.top + bounds.height / 2);
    };

    // Déplace la vue de (dx, dy) pixels à l'écran.
    const panBy = (dx, dy) => {
        const bounds = svg.getBoundingClientRect();
        if (!bounds.width) {
            return;
        }
        const unitsPerPixel = width() / bounds.width;
        view.x -= dx * unitsPerPixel;
        view.y -= dy * unitsPerPixel;
        apply();
    };

    svg.addEventListener("wheel", event => {
        event.preventDefault();
        const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? 400 : 1);
        zoomAt(view.scale * Math.exp(-delta * 0.0015), event.clientX, event.clientY);
    }, { passive: false });

    // Glisser (un pointeur) et pincer (deux pointeurs).
    const pointers = new Map();
    let pinchDistance = 0;
    let travelled = 0;
    let dragged = false;

    const midpoint = () => {
        const [a, b] = [...pointers.values()];
        return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, distance: Math.hypot(a.x - b.x, a.y - b.y) };
    };

    svg.addEventListener("pointerdown", event => {
        if (event.pointerType === "mouse" && event.button !== 0) {
            return;
        }

        if (pointers.size === 0) {
            travelled = 0;
            dragged = false;
        }

        pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });

        if (pointers.size === 2) {
            pinchDistance = midpoint().distance;
        }
    });

    svg.addEventListener("pointermove", event => {
        const previous = pointers.get(event.pointerId);
        if (!previous) {
            return;
        }

        if (pointers.size === 1) {
            const dx = event.clientX - previous.x;
            const dy = event.clientY - previous.y;
            pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
            travelled += Math.hypot(dx, dy);

            if (travelled > DRAG_THRESHOLD) {
                dragged = true;
            }

            if (dragged && view.scale > 1) {
                container.classList.add("is-panning");
                panBy(dx, dy);
            }
            return;
        }

        if (pointers.size === 2) {
            const before = midpoint();
            pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
            const after = midpoint();
            dragged = true;

            if (pinchDistance > 0 && after.distance > 0) {
                zoomAt(view.scale * after.distance / pinchDistance, after.x, after.y);
            }
            panBy(after.x - before.x, after.y - before.y);
            pinchDistance = after.distance;
        }
    });

    const release = event => {
        pointers.delete(event.pointerId);
        pinchDistance = pointers.size === 2 ? midpoint().distance : 0;

        if (pointers.size === 0) {
            container.classList.remove("is-panning");
        }
    };

    svg.addEventListener("pointerup", release);
    svg.addEventListener("pointercancel", release);
    svg.addEventListener("pointerleave", release);

    bar.addEventListener("click", event => {
        const action = event.target.closest("[data-zoom]")?.dataset.zoom;
        if (action === "in") {
            zoomAtCenter(view.scale * 1.5);
        } else if (action === "out") {
            zoomAtCenter(view.scale / 1.5);
        } else if (action === "reset") {
            zoomAtCenter(1);
        }
    });

    range.addEventListener("input", () => zoomAtCenter(Math.pow(MAX_ZOOM, range.value / 100)));

    apply();

    return {
        // Vrai (une seule fois) si le dernier geste était un glissement et non un clic.
        consumeDrag() {
            const result = dragged;
            dragged = false;
            return result;
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
