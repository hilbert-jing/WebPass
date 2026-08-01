(() => {
    const root = document.documentElement;
    root.setAttribute("data-js-loading", "");

    window.addEventListener("load", () => {
        if (root.hasAttribute("data-js-enabled")) return;

        root.removeAttribute("data-js-loading");
        document.querySelectorAll("[data-drawer]").forEach(drawer => {
            drawer.removeAttribute("aria-hidden");
        });
        document.querySelectorAll(
            "[data-drawer-open], [data-nav-toggle]").forEach(opener => {
                opener.setAttribute("aria-expanded", "false");
            });
    }, { once: true });
})();
