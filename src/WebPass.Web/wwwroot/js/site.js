(() => {
    const focusable = "a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex]:not([tabindex='-1'])";
    const drawerOpeners = new WeakMap();
    const mobileNavigation = window.matchMedia?.("(max-width: 767px)") ?? null;

    function getDrawer(id) {
        if (!id) return null;
        return document.getElementById(id.replace(/^#/, ""));
    }

    function findDrawerOpener(drawer) {
        return Array.from(document.querySelectorAll(
            "[data-drawer-open], [data-nav-toggle]"))
            .find(candidate => {
                const target = candidate.dataset.drawerOpen ??
                    candidate.getAttribute("aria-controls");
                return getDrawer(target) === drawer;
            });
    }

    function moveFocusOutsideBeforeHide(drawer, preferredTarget) {
        if (!drawer.contains(document.activeElement)) return;
        (preferredTarget ??
            drawerOpeners.get(drawer) ??
            document.getElementById("main-content"))?.focus();
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

        const opener = drawerOpeners.get(drawer) ?? findDrawerOpener(drawer);
        moveFocusOutsideBeforeHide(drawer, opener);
        opener?.setAttribute("aria-expanded", "false");
        drawer.removeAttribute("data-open");
        if (drawer.hasAttribute("aria-hidden")) {
            drawer.setAttribute("aria-hidden", "true");
        }
        drawerOpeners.delete(drawer);
    }

    document.querySelectorAll("[data-drawer]").forEach(drawer => {
        const isOpen = drawer.hasAttribute("data-open");
        const opener = findDrawerOpener(drawer);
        const isResponsiveNavigation =
            opener?.hasAttribute("data-nav-toggle") === true;

        if (isResponsiveNavigation && !mobileNavigation?.matches) {
            drawer.removeAttribute("aria-hidden");
            opener.setAttribute("aria-expanded", "false");
            return;
        }

        if (!isOpen) moveFocusOutsideBeforeHide(drawer, opener);
        drawer.setAttribute("aria-hidden", isOpen ? "false" : "true");
        if (!opener) return;

        opener.setAttribute("aria-expanded", isOpen ? "true" : "false");
        if (isOpen) drawerOpeners.set(drawer, opener);
    });

    async function copyText(button) {
        const selector = button.dataset.copyTarget;
        const statusSelector = button.dataset.copyStatusTarget;
        const target = selector ? document.querySelector(selector) : null;
        const status = statusSelector
            ? document.querySelector(statusSelector)
            : null;
        const text = target?.textContent?.trim() ?? "";

        const showFeedback = message => {
            if (!status) return;
            status.textContent = message;
            window.setTimeout(() => {
                status.textContent = "";
            }, 1800);
        };

        if (!text || !navigator.clipboard?.writeText) {
            showFeedback("复制失败，请手动选择");
            return;
        }

        try {
            await navigator.clipboard.writeText(text);
            showFeedback("已复制");
        } catch {
            showFeedback("复制失败，请手动选择");
        }
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

    function syncMobileNavigation() {
        document.querySelectorAll("[data-nav-toggle]").forEach(button => {
            const sidebar = document.getElementById(
                button.getAttribute("aria-controls"));
            if (!sidebar) return;

            if (!mobileNavigation?.matches) {
                const wasOpen = sidebar.hasAttribute("data-open");
                const firstNavigationLink =
                    sidebar.querySelector(".primary-nav a");
                if (wasOpen) {
                    moveFocusOutsideBeforeHide(
                        sidebar,
                        firstNavigationLink);
                }
                sidebar.removeAttribute("data-open");
                sidebar.removeAttribute("aria-hidden");
                button.setAttribute("aria-expanded", "false");
                return;
            }

            const isOpen = sidebar.hasAttribute("data-open");
            if (!isOpen) moveFocusOutsideBeforeHide(sidebar, button);
            sidebar.setAttribute("aria-hidden", isOpen ? "false" : "true");
            button.setAttribute("aria-expanded", isOpen ? "true" : "false");
        });
    }

    syncMobileNavigation();
    mobileNavigation?.addEventListener("change", syncMobileNavigation);

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
