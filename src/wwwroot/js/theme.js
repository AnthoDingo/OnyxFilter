// Gestion du thème (auto / sombre / clair), persisté dans localStorage.
(function () {
    const STORAGE_KEY = "onyxfilter-theme";
    const media = window.matchMedia("(prefers-color-scheme: dark)");

    function getPreference() {
        const stored = window.localStorage.getItem(STORAGE_KEY);
        return stored === "dark" || stored === "light" ? stored : "auto";
    }

    function resolve(preference) {
        return preference === "auto" ? (media.matches ? "dark" : "light") : preference;
    }

    function apply() {
        document.documentElement.setAttribute("data-bs-theme", resolve(getPreference()));
    }

    function setPreference(preference) {
        if (preference === "dark" || preference === "light") {
            window.localStorage.setItem(STORAGE_KEY, preference);
        } else {
            window.localStorage.removeItem(STORAGE_KEY);
        }
        apply();
    }

    media.addEventListener("change", function () {
        if (getPreference() === "auto") {
            apply();
        }
    });

    window.onyxTheme = {
        getPreference: getPreference,
        setPreference: setPreference
    };

    apply();
})();
