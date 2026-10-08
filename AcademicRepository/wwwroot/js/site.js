(() => {
    const toggle = document.querySelector("[data-sidebar-toggle]");
    const closeButton = document.querySelector("[data-sidebar-close]");
    const sidebar = document.getElementById("appSidebar");
    const setSidebarOpen = (open) => {
        document.body.classList.toggle("sidebar-open", open);
        if (sidebar && window.matchMedia("(max-width: 991.98px)").matches) {
            sidebar.inert = !open;
            sidebar.setAttribute("aria-hidden", String(!open));
        }
        if (toggle) {
            toggle.setAttribute("aria-expanded", String(open));
            toggle.setAttribute("aria-label", open ? "Close navigation menu" : "Open navigation menu");
        }
    };

    if (sidebar && window.matchMedia("(max-width: 991.98px)").matches) {
        sidebar.inert = true;
        sidebar.setAttribute("aria-hidden", "true");
    }
    window.matchMedia("(max-width: 991.98px)").addEventListener("change", (event) => {
        if (!sidebar) return;
        if (event.matches) {
            setSidebarOpen(false);
        } else {
            sidebar.inert = false;
            sidebar.removeAttribute("aria-hidden");
            setSidebarOpen(false);
        }
    });
    toggle?.addEventListener("click", () => setSidebarOpen(toggle.getAttribute("aria-expanded") !== "true"));
    closeButton?.addEventListener("click", () => {
        setSidebarOpen(false);
        toggle?.focus();
    });
    sidebar?.querySelectorAll("a").forEach((link) => link.addEventListener("click", () => setSidebarOpen(false)));
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && document.body.classList.contains("sidebar-open")) {
            setSidebarOpen(false);
            toggle?.focus();
        }
    });

    document.querySelectorAll("form[data-loading-text]").forEach((form) => {
        form.addEventListener("submit", () => {
            const button = form.querySelector("button[type=submit]:focus, button[type=submit]");
            if (!button || button.disabled) return;
            button.dataset.originalText ??= button.textContent;
            button.textContent = form.dataset.loadingText;
            button.setAttribute("aria-busy", "true");
            button.disabled = true;
        }, { once: true });
    });

    document.querySelectorAll("input[type=file]").forEach((input) => {
        const output = input.closest("form")?.querySelector("[aria-live]");
        input.addEventListener("change", () => {
            if (!output) return;
            const file = input.files?.[0];
            output.textContent = file ? `${file.name} · ${(file.size / 1024 / 1024).toFixed(2)} MB` : "No file selected.";
        });
    });
})();
