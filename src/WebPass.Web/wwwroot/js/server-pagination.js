(() => {
    document.querySelectorAll("[data-page-size]").forEach(form => {
        const input = form.querySelector('input[name="Query.Take"]');
        if (!input) return;

        let submitting = false;
        const apply = () => {
            if (submitting || input.value === input.defaultValue || !form.reportValidity()) return;
            submitting = true;
            form.requestSubmit();
        };

        input.addEventListener("change", apply);
        input.addEventListener("keydown", event => {
            if (event.key !== "Enter") return;
            event.preventDefault();
            apply();
        });
    });
})();
