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
    document.querySelectorAll(".side-link.active").forEach((link) => link.setAttribute("aria-current", "page"));
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            const openActionMenu = document.activeElement?.closest("details.action-menu[open]");
            if (openActionMenu) {
                openActionMenu.open = false;
                openActionMenu.querySelector(":scope > summary")?.focus();
            }
        }
        if (event.key === "Escape" && document.body.classList.contains("sidebar-open")) {
            setSidebarOpen(false);
            toggle?.focus();
        }
    });

    const confirmationDialog = document.querySelector("[data-confirm-dialog]");
    const confirmationMessage = confirmationDialog?.querySelector("[data-confirm-message]");
    const acceptConfirmation = confirmationDialog?.querySelector("[data-confirm-accept]");
    const cancelConfirmation = confirmationDialog?.querySelector("[data-confirm-cancel]");
    let pendingConfirmation = null;

    document.addEventListener("click", (event) => {
        const button = event.target.closest("button[data-confirm]");
        if (!button) return;
        const form = button.closest("form");
        if (!form) return;
        event.preventDefault();
        if (!confirmationDialog?.showModal) {
            if (window.confirm(button.dataset.confirm)) form.requestSubmit(button);
            return;
        }
        pendingConfirmation = { form, button };
        confirmationMessage.textContent = button.dataset.confirm;
        confirmationDialog.showModal();
        cancelConfirmation?.focus();
    });

    acceptConfirmation?.addEventListener("click", () => {
        const pending = pendingConfirmation;
        pendingConfirmation = null;
        confirmationDialog.close();
        if (pending?.form.isConnected && !pending.button.disabled) pending.form.requestSubmit(pending.button);
    });
    cancelConfirmation?.addEventListener("click", () => {
        const button = pendingConfirmation?.button;
        pendingConfirmation = null;
        confirmationDialog.close();
        button?.focus();
    });
    confirmationDialog?.addEventListener("close", () => { pendingConfirmation = null; });

    const loadingLabel = (button, form) => {
        if (button?.dataset.loadingText) return button.dataset.loadingText;
        if (form.dataset.loadingText) return form.dataset.loadingText;
        if (form.matches(".filter-panel")) return "Applying filters…";
        const label = button?.textContent?.trim().toLowerCase() ?? "";
        if (label.includes("upload")) return "Uploading…";
        if (label.includes("deactivate") || label.includes("activate")) return "Updating…";
        if (label.includes("delete") || label.includes("remove")) return "Deleting…";
        if (label.includes("reject")) return "Rejecting…";
        if (label.includes("approv")) return "Approving…";
        if (label.includes("resubmit") || label.includes("submit")) return "Submitting…";
        if (label.includes("revision")) return "Starting…";
        return form.method.toLowerCase() === "get" ? "Searching…" : "Working…";
    };

    document.querySelectorAll("form").forEach((form) => {
        form.addEventListener("submit", (event) => {
            if (form.dataset.submitting === "true") {
                event.preventDefault();
                return;
            }
            form.dataset.submitting = "true";
            const uploadStatus = form.querySelector("[data-upload-status]");
            if (uploadStatus) uploadStatus.textContent = "Uploading. Please wait for server confirmation.";
            const submitter = event.submitter instanceof HTMLElement
                ? event.submitter
                : form.querySelector("button[type=submit], input[type=submit]");
            if (submitter?.name && submitter.value) {
                const mirror = document.createElement("input");
                mirror.type = "hidden";
                mirror.name = submitter.name;
                mirror.value = submitter.value;
                form.append(mirror);
            }
            form.querySelectorAll("button[type=submit], input[type=submit]").forEach((button) => {
                if (button === submitter) {
                    button.dataset.originalText ??= button instanceof HTMLInputElement ? button.value : button.textContent;
                    const label = loadingLabel(button, form);
                    if (button instanceof HTMLInputElement) button.value = label;
                    else button.textContent = label;
                    button.setAttribute("aria-busy", "true");
                }
                button.disabled = true;
                button.dataset.submitPending = "true";
            });
        });
    });

    document.querySelectorAll("[data-valmsg-for]").forEach((message) => {
        const fieldName = message.getAttribute("data-valmsg-for");
        const form = message.closest("form");
        const controls = Array.from(form?.elements ?? []);
        const control = controls.find((element) => element.name === fieldName && element.type !== "hidden");
        if (!control) return;
        message.id ||= `${control.id || fieldName}-validation-message`;
        message.setAttribute("aria-live", "polite");
        const describedBy = new Set((control.getAttribute("aria-describedby") || "").split(/\s+/).filter(Boolean));
        describedBy.add(message.id);
        control.setAttribute("aria-describedby", Array.from(describedBy).join(" "));
        const updateInvalidState = () => {
            if (message.textContent.trim()) control.setAttribute("aria-invalid", "true");
            else control.removeAttribute("aria-invalid");
        };
        updateInvalidState();
        control.addEventListener("input", () => window.setTimeout(updateInvalidState, 0));
        control.addEventListener("change", () => window.setTimeout(updateInvalidState, 0));
    });

    window.addEventListener("pageshow", () => {
        document.querySelectorAll("[data-submit-pending]").forEach((button) => {
            button.disabled = false;
            button.removeAttribute("aria-busy");
            delete button.dataset.submitPending;
            const original = button.dataset.originalText;
            if (original !== undefined) {
                if (button instanceof HTMLInputElement) button.value = original;
                else button.textContent = original;
            }
        });
        document.querySelectorAll("form[data-submitting]").forEach((form) => delete form.dataset.submitting);
    });

    document.querySelectorAll("a[data-loading-link]").forEach((link) => {
        link.addEventListener("click", () => {
            if (link.dataset.originalText === undefined) link.dataset.originalText = link.textContent;
            link.textContent = link.dataset.loadingText || "Starting download…";
            link.setAttribute("aria-busy", "true");
            window.setTimeout(() => {
                if (!link.isConnected) return;
                link.textContent = link.dataset.originalText;
                link.removeAttribute("aria-busy");
            }, 4000);
        });
    });

    document.querySelectorAll("[data-toast-dismiss]").forEach((button) => {
        button.addEventListener("click", () => button.closest(".feedback-toast")?.remove());
    });

    const showFeedback = (kind, label, message, role) => {
        let region = document.querySelector(".toast-region");
        if (!region) {
            region = document.createElement("section");
            region.className = "toast-region";
            region.setAttribute("aria-label", "Notifications");
            document.body.append(region);
        }
        const toast = document.createElement("div");
        toast.className = `feedback-toast feedback-toast-${kind}`;
        toast.setAttribute("role", role);
        toast.setAttribute("aria-live", role === "alert" ? "assertive" : "polite");
        toast.setAttribute("aria-atomic", "true");
        const heading = document.createElement("div");
        heading.className = "feedback-toast-heading";
        const title = document.createElement("strong");
        title.textContent = label;
        const dismiss = document.createElement("button");
        dismiss.type = "button";
        dismiss.className = "toast-dismiss";
        dismiss.setAttribute("aria-label", `Dismiss ${label} notification`);
        dismiss.textContent = "×";
        dismiss.addEventListener("click", () => toast.remove());
        const text = document.createElement("p");
        text.textContent = message;
        heading.append(title, dismiss);
        toast.append(heading, text);
        region.append(toast);
    };
    window.addEventListener("offline", () => showFeedback("error", "Connection lost", "Check your network connection and try again.", "alert"));
    window.addEventListener("online", () => showFeedback("info", "Connection restored", "You are back online.", "status"));

    document.querySelectorAll("input[type=file]").forEach((input) => {
        const output = input.closest("form")?.querySelector("[aria-live]");
        input.addEventListener("change", () => {
            if (!output) return;
            const file = input.files?.[0];
            output.textContent = file ? `${file.name} · ${(file.size / 1024 / 1024).toFixed(2)} MB` : "No file selected.";
        });
    });
})();
