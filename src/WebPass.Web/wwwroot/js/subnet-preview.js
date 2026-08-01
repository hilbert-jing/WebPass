(() => {
    const form = document.querySelector("[data-subnet-preview-form]");
    const button = form?.querySelector("[data-subnet-preview]");
    const result = form?.querySelector("[data-subnet-preview-result]");
    if (!form || !button || !result) return;

    function appendMetric(list, label, value) {
        const item = document.createElement("li");
        const name = document.createElement("span");
        const data = document.createElement("strong");
        name.textContent = label;
        data.textContent = String(value);
        data.className = "data-value";
        item.append(name, " ", data);
        list.append(item);
    }

    function renderPreview(preview) {
        result.textContent = "";
        result.setAttribute("role", "status");

        const rail = document.createElement("section");
        const heading = document.createElement("h3");
        const list = document.createElement("ol");
        rail.setAttribute("data-ip-rail", "");
        rail.className = "form-section";
        heading.textContent = "CIDR 地址预览";

        appendMetric(list, "网络地址", preview.networkAddress);
        appendMetric(list, "可用地址数", preview.usableAddressCount);
        appendMetric(list, "广播地址", preview.broadcastAddress);
        rail.append(heading, list);
        result.append(rail);
    }

    function renderError(message) {
        result.textContent = message;
        result.setAttribute("role", "alert");
    }

    button.addEventListener("click", async () => {
        button.disabled = true;
        result.setAttribute("role", "status");
        result.textContent = "正在计算地址范围…";

        try {
            const fields = new URLSearchParams(new FormData(form));
            const response = await fetch("?handler=Preview", {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8",
                },
                body: fields,
            });
            const payload = await response.json().catch(() => ({}));
            if (!response.ok) {
                const message = typeof payload.error === "string"
                    ? payload.error
                    : "无法预览网段，请稍后重试。";
                renderError(message);
                return;
            }

            renderPreview(payload);
        } catch {
            renderError("无法预览网段，请检查网络连接后重试。");
        } finally {
            button.disabled = false;
        }
    });
})();
