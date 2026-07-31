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

    document.querySelectorAll(".drawer[data-drawer]").forEach(drawer => {
        const isOpen = drawer.hasAttribute("data-open");
        drawer.setAttribute("aria-hidden", isOpen ? "false" : "true");

        const opener = Array.from(document.querySelectorAll("[data-drawer-open]"))
            .find(candidate => getDrawer(candidate.dataset.drawerOpen) === drawer);
        if (!opener) return;

        opener.setAttribute("aria-expanded", isOpen ? "true" : "false");
        if (isOpen) drawerOpeners.set(drawer, opener);
    });

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

    document.querySelectorAll("[data-upload-zone]").forEach(zone => {
        const input = zone.querySelector("[data-upload-input]");
        const fileName = zone.querySelector("[data-upload-name]");
        if (!(input instanceof HTMLInputElement) || input.type !== "file") return;

        const updateFileName = () => {
            if (!fileName) return;
            fileName.textContent = input.files?.[0]?.name || "尚未选择文件";
        };

        input.addEventListener("change", updateFileName);

        ["dragenter", "dragover"].forEach(eventName => {
            zone.addEventListener(eventName, event => {
                event.preventDefault();
                zone.setAttribute("data-dragging", "");
            });
        });

        zone.addEventListener("dragleave", () => {
            zone.removeAttribute("data-dragging");
        });

        zone.addEventListener("drop", event => {
            event.preventDefault();
            zone.removeAttribute("data-dragging");
            const files = event.dataTransfer?.files;
            if (!files || files.length !== 1) return;

            const transfer = new DataTransfer();
            transfer.items.add(files[0]);
            input.files = transfer.files;
            updateFileName();
        });
    });

    document.querySelectorAll("[data-export-format]").forEach(format => {
        if (!(format instanceof HTMLSelectElement)) return;
        const submit = format.form?.querySelector("[data-export-submit]");
        if (!(submit instanceof HTMLButtonElement)) return;

        const updateExportLabel = () => {
            submit.textContent = format.value === "Csv"
                ? "下载 CSV"
                : "下载 XLSX";
        };

        format.addEventListener("change", updateExportLabel);
        updateExportLabel();
    });
})();
