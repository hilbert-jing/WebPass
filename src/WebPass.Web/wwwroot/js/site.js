(() => {
    const focusable = "a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex='-1'])";
    const drawerOpeners = new WeakMap();

    function getDrawer(id) {
        if (!id) return null;
        return document.getElementById(id.replace(/^#/, ""));
    }

    function openDrawer(id, opener) {
        const drawer = getDrawer(id);
        if (!drawer) return;

        drawer.setAttribute("data-open", "");
        if (drawer.hasAttribute("aria-hidden")) {
            drawer.setAttribute("aria-hidden", "false");
        }
        drawerOpeners.set(drawer, opener);
        opener?.setAttribute("aria-expanded", "true");
        const initialFocus = drawer.querySelector("[data-drawer-initial-focus]") ??
            drawer.querySelector(focusable);
        initialFocus?.focus();
    }

    function closeDrawer(drawer) {
        if (!drawer) return;

        drawer.removeAttribute("data-open");
        if (drawer.hasAttribute("aria-hidden")) {
            drawer.setAttribute("aria-hidden", "true");
        }

        const opener = drawerOpeners.get(drawer);
        opener?.setAttribute("aria-expanded", "false");
        opener?.focus();
        drawerOpeners.delete(drawer);
    }

    async function copyText(button) {
        const target = getDrawer(button.dataset.copy) ||
            (button.dataset.copy ? document.querySelector(button.dataset.copy) : null);
        const value = target?.value ?? target?.textContent ?? "";
        const text = value.trim();
        if (!text || !navigator.clipboard) return;

        const originalLabel = button.textContent;
        try {
            await navigator.clipboard.writeText(text);
            button.textContent = button.dataset.copyLabel || "已复制";
        } catch {
            button.textContent = button.dataset.copyErrorLabel || "复制失败";
        }

        window.setTimeout(() => {
            button.textContent = originalLabel;
        }, 1800);
    }

    document.querySelectorAll("[data-nav-toggle]").forEach(button => {
        button.addEventListener("click", () => {
            const sidebar = document.getElementById(button.getAttribute("aria-controls"));
            if (!sidebar) return;
            if (sidebar.hasAttribute("data-open")) {
                closeDrawer(sidebar);
                return;
            }
            openDrawer(sidebar.id, button);
        });
    });

    document.addEventListener("click", event => {
        const target = event.target instanceof Element ? event.target : null;
        const opener = target?.closest("[data-drawer-open]");
        const closer = target?.closest("[data-drawer-close]");
        const copy = target?.closest("[data-copy]");
        if (opener) openDrawer(opener.dataset.drawerOpen, opener);
        if (closer) closeDrawer(closer.closest("[data-drawer]"));
        if (copy) copyText(copy);
    });

    document.addEventListener("keydown", event => {
        if (event.key !== "Escape") return;
        const drawers = document.querySelectorAll("[data-drawer][data-open]");
        closeDrawer(drawers[drawers.length - 1]);
    });

    document.addEventListener("submit", event => {
        const button = event.submitter;
        if (!button?.dataset.submitLabel) return;
        button.disabled = true;
        button.textContent = button.dataset.submitLabel;
    });
})();
