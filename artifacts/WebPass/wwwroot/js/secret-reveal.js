(() => {
    const clearAfterMilliseconds = 30_000;
    const timers = new Map();

    function clear(output) {
        output.textContent = "";
        output.hidden = true;
        const timer = timers.get(output);
        if (timer) {
            window.clearTimeout(timer);
            timers.delete(output);
        }
    }

    async function reveal(button) {
        const output = document.getElementById(button.dataset.output);
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
        if (!output || !token) {
            return;
        }

        clear(output);
        const body = new URLSearchParams({ __RequestVerificationToken: token });
        const response = await fetch(`/secrets/reveal?assetId=${encodeURIComponent(button.dataset.assetId)}`, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8" },
            body
        });
        if (response.status === 403) {
            window.location.assign("/secrets/reauthenticate?returnUrl=%2Fservers");
            return;
        }
        if (!response.ok) {
            output.textContent = "Password unavailable.";
            output.hidden = false;
            return;
        }

        const result = await response.json();
        output.textContent = result.password;
        output.hidden = false;
        timers.set(output, window.setTimeout(() => clear(output), clearAfterMilliseconds));
    }

    document.addEventListener("click", event => {
        const button = event.target.closest("[data-secret-reveal]");
        if (button) {
            reveal(button);
        }
    });
    window.addEventListener("pagehide", () => {
        document.querySelectorAll("[data-secret-output]").forEach(clear);
    });
})();
