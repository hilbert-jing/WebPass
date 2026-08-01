(() => {
    const clearAfterSeconds = 30;
    const timers = new Map();
    let activeController = null;

    function stopTimers(panel) {
        const timer = timers.get(panel);
        if (!timer) {
            return;
        }

        window.clearInterval(timer.intervalId);
        window.clearTimeout(timer.timeoutId);
        timers.delete(panel);
    }

    function clearPanel(panel) {
        stopTimers(panel);
        const value = panel.querySelector("[data-secret-value]");
        const countdown = panel.querySelector("[data-secret-countdown]");
        const status = panel.querySelector("[data-secret-status]");
        if (value) {
            value.textContent = "";
        }
        if (countdown) {
            countdown.textContent = String(clearAfterSeconds);
        }
        if (status) {
            status.textContent = `服务器密码将在 ${clearAfterSeconds} 秒后自动隐藏`;
        }
        panel.hidden = true;
    }

    function clearAll() {
        activeController?.abort();
        activeController = null;
        document.querySelectorAll("[data-secret-panel]").forEach(clearPanel);
    }

    function showFailure(panel) {
        const status = panel.querySelector("[data-secret-status]");
        if (status) {
            status.textContent = "暂时无法查看密码，请重试";
        }
        panel.hidden = false;
    }

    function startCountdown(panel) {
        const countdown = panel.querySelector("[data-secret-countdown]");
        let remainingSeconds = clearAfterSeconds;
        if (countdown) {
            countdown.textContent = String(remainingSeconds);
        }

        const intervalId = window.setInterval(() => {
            remainingSeconds -= 1;
            if (countdown) {
                countdown.textContent = String(Math.max(remainingSeconds, 0));
            }
        }, 1_000);
        const timeoutId = window.setTimeout(
            () => clearPanel(panel),
            clearAfterSeconds * 1_000);
        timers.set(panel, { intervalId, timeoutId });
    }

    async function reveal(button) {
        clearAll();
        const panel = document.getElementById(button.dataset.output);
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        if (!panel || !token || !button.dataset.assetId) {
            return;
        }

        const controller = new AbortController();
        activeController = controller;
        try {
            const body = new URLSearchParams({ __RequestVerificationToken: token });
            const response = await fetch(
                `/secrets/reveal?assetId=${encodeURIComponent(button.dataset.assetId)}`,
                {
                    method: "POST",
                    credentials: "same-origin",
                    headers: { "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8" },
                    body,
                    signal: controller.signal
                });
            if (controller.signal.aborted || activeController !== controller) {
                return;
            }
            if (response.status === 403) {
                window.location.assign("/secrets/reauthenticate?returnUrl=%2Fservers");
                return;
            }
            if (!response.ok) {
                showFailure(panel);
                return;
            }

            const result = await response.json();
            if (controller.signal.aborted || activeController !== controller) {
                return;
            }
            const value = panel.querySelector("[data-secret-value]");
            if (!value || typeof result.password !== "string") {
                showFailure(panel);
                return;
            }

            value.textContent = result.password;
            panel.hidden = false;
            startCountdown(panel);
        } catch {
            if (!controller.signal.aborted) {
                showFailure(panel);
            }
        } finally {
            if (activeController === controller) {
                activeController = null;
            }
        }
    }

    async function copySecret(button) {
        const panel = button.closest("[data-secret-panel]");
        const value = panel?.querySelector("[data-secret-value]");
        const status = panel?.querySelector("[data-secret-status]");
        if (!value?.textContent) {
            return;
        }

        try {
            await navigator.clipboard.writeText(value.textContent);
            if (status) {
                status.textContent = "已复制";
            }
        } catch {
            if (status) {
                status.textContent = "复制失败，请手动复制";
            }
        }
    }

    document.addEventListener("click", event => {
        if (!(event.target instanceof Element)) {
            return;
        }

        const revealButton = event.target.closest("[data-secret-reveal]");
        if (revealButton) {
            reveal(revealButton);
            return;
        }

        const copyButton = event.target.closest("[data-secret-copy]");
        if (copyButton) {
            copySecret(copyButton);
        }
    });
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "hidden") {
            clearAll();
        }
    });
    window.addEventListener("pagehide", clearAll);
    document.documentElement.setAttribute("data-secret-reveal-ready", "");
})();
