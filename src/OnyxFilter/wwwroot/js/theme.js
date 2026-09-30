// Comportements de la coque d'interface, en JavaScript pur : le layout (MainLayout) est rendu côté
// serveur en statique, ses boutons ne peuvent donc pas s'appuyer sur des @onclick Blazor.
//  - Thème (auto / sombre / clair), persisté dans localStorage et appliqué via data-bs-theme sur <html>.
//  - Ouverture/fermeture de la barre latérale sur petit écran.
(function () {
    const STORAGE_KEY = "onyxfilter-theme";
    const media = window.matchMedia("(prefers-color-scheme: dark)");

    function getPreference() {
        try {
            const stored = window.localStorage.getItem(STORAGE_KEY);
            return stored === "dark" || stored === "light" ? stored : "auto";
        } catch {
            return "auto";
        }
    }

    function resolve(preference) {
        return preference === "auto" ? (media.matches ? "dark" : "light") : preference;
    }

    // Reflète la préférence sur les boutons du sélecteur de thème (barre latérale).
    function syncThemeButtons() {
        const preference = getPreference();
        document.querySelectorAll("[data-theme-choice]").forEach(function (button) {
            button.setAttribute("aria-pressed", button.getAttribute("data-theme-choice") === preference ? "true" : "false");
        });
    }

    function apply() {
        document.documentElement.setAttribute("data-bs-theme", resolve(getPreference()));
        syncThemeButtons();
    }

    function setPreference(preference) {
        try {
            if (preference === "dark" || preference === "light") {
                window.localStorage.setItem(STORAGE_KEY, preference);
            } else {
                window.localStorage.removeItem(STORAGE_KEY);
            }
        } catch {
            // Stockage indisponible (navigation privée stricte) : le choix vaut pour la page en cours.
        }
        apply();
    }

    function setNavOpen(open) {
        const shell = document.querySelector("[data-shell]");
        if (!shell) {
            return;
        }
        shell.classList.toggle("is-nav-open", open);
        document.querySelectorAll("[data-nav-toggle]").forEach(function (button) {
            button.setAttribute("aria-expanded", open ? "true" : "false");
        });
    }

    document.addEventListener("click", function (event) {
        const target = event.target instanceof Element ? event.target : null;
        if (!target) {
            return;
        }

        const themeButton = target.closest("[data-theme-choice]");
        if (themeButton) {
            setPreference(themeButton.getAttribute("data-theme-choice"));
            return;
        }

        if (target.closest("[data-nav-toggle]")) {
            const shell = document.querySelector("[data-shell]");
            setNavOpen(!(shell && shell.classList.contains("is-nav-open")));
            return;
        }

        // Fond assombri, bouton de fermeture ou lien de navigation : on referme le menu mobile.
        if (target.closest("[data-nav-close]") || target.closest(".sidebar a")) {
            setNavOpen(false);
        }
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            setNavOpen(false);
        }
    });

    media.addEventListener("change", function () {
        if (getPreference() === "auto") {
            apply();
        }
    });

    // La navigation améliorée de Blazor resynchronise le DOM (y compris les attributs de <html>) avec
    // la réponse du serveur : on réapplique le thème et l'état des boutons après chaque navigation.
    document.addEventListener("DOMContentLoaded", function () {
        syncThemeButtons();
        if (window.Blazor && typeof window.Blazor.addEventListener === "function") {
            window.Blazor.addEventListener("enhancedload", apply);
        }
    });

    window.onyxTheme = {
        getPreference: getPreference,
        setPreference: setPreference
    };

    apply();
})();
