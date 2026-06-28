(() => {
    let pendingDeleteForm = null;

    function getModal() {
        return document.querySelector("[data-alert-delete-modal]");
    }

    function openModal(form) {
        const modal = getModal();
        if (!modal) {
            form.submit();
            return;
        }

        pendingDeleteForm = form;
        modal.hidden = false;
        modal.classList.add("open");
        modal.querySelector("[data-alert-delete-cancel]")?.focus();
    }

    function closeModal() {
        const modal = getModal();
        if (!modal) {
            return;
        }

        modal.classList.remove("open");
        modal.hidden = true;
        pendingDeleteForm = null;
    }

    function bindAlertActions() {
        document.querySelectorAll("[data-confirm-delete-alert]:not([data-delete-bound])").forEach((button) => {
            button.dataset.deleteBound = "true";
            button.addEventListener("click", () => {
                const form = button.closest("form");
                if (form) {
                    openModal(form);
                }
            });
        });

        const modal = getModal();
        if (!modal || modal.dataset.deleteModalBound) {
            return;
        }

        modal.dataset.deleteModalBound = "true";
        modal.querySelectorAll("[data-alert-delete-cancel]").forEach((button) => {
            button.addEventListener("click", closeModal);
        });
        modal.querySelector("[data-alert-delete-confirm]")?.addEventListener("click", () => {
            const form = pendingDeleteForm;
            closeModal();
            form?.submit();
        });
    }

    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            closeModal();
        }
    });

    document.addEventListener("DOMContentLoaded", bindAlertActions);
    window.addEventListener("load", bindAlertActions);
    window.addEventListener("pageshow", bindAlertActions);
    document.addEventListener("enhancedload", bindAlertActions);

    new MutationObserver(bindAlertActions).observe(document.documentElement, {
        childList: true,
        subtree: true
    });
})();
