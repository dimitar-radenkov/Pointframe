// UI module: handles install command copy interactions, the winget/scoop
// package-manager toggle, and related tracking events.
export function initCopyCommand(trackEvent) {
    const copyButton = document.getElementById("copy-command-button");
    const copyStatus = document.getElementById("copy-command-status");
    const commandText = document.getElementById("copy-command-text");
    const tabs = Array.from(document.querySelectorAll(".install-tab"));

    if (!(copyButton instanceof HTMLButtonElement) || !(copyStatus instanceof HTMLElement)) {
        return;
    }

    let activeCommand = "winget install DimitarRadenkov.Pointframe";
    let activeManager = "winget";

    const setStatus = (message, kind) => {
        copyStatus.textContent = message;
        copyStatus.className = kind ? `copy-status is-${kind}` : "copy-status";
    };

    for (const tab of tabs) {
        tab.addEventListener("click", () => {
            for (const other of tabs) {
                const isActive = other === tab;
                other.classList.toggle("is-active", isActive);
                other.setAttribute("aria-pressed", isActive ? "true" : "false");
            }

            activeCommand = tab.dataset.command || activeCommand;
            activeManager = tab.dataset.manager || activeManager;

            if (commandText instanceof HTMLElement) {
                commandText.textContent = activeCommand;
            }

            setStatus("", "");
            trackEvent("install_manager_selected", {
                cta_location: "hero",
                manager: activeManager
            });
        });
    }

    const fallbackCopy = (text) => {
        const textArea = document.createElement("textarea");
        textArea.value = text;
        textArea.setAttribute("readonly", "readonly");
        textArea.style.position = "fixed";
        textArea.style.opacity = "0";
        document.body.appendChild(textArea);
        textArea.select();

        const didCopy = document.execCommand("copy");
        document.body.removeChild(textArea);

        if (!didCopy) {
            throw new Error("Fallback copy failed.");
        }
    };

    copyButton.addEventListener("click", async () => {
        copyButton.disabled = true;
        setStatus("Copying command...", "");

        const command = activeCommand;

        try {
            if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") {
                await navigator.clipboard.writeText(command);
            }
            else {
                fallbackCopy(command);
            }

            trackEvent("winget_command_copied", {
                cta_location: "hero",
                manager: activeManager,
                command
            });
            setStatus("Command copied to clipboard.", "success");
        }
        catch {
            try {
                fallbackCopy(command);
                trackEvent("winget_command_copied", {
                    cta_location: "hero",
                    manager: activeManager,
                    command,
                    copied_via: "fallback"
                });
                setStatus("Command copied to clipboard.", "success");
            }
            catch {
                trackEvent("winget_command_copy_failed", {
                    cta_location: "hero",
                    manager: activeManager,
                    command
                });
                setStatus("Could not copy automatically. Please copy the command manually.", "error");
            }
        }
        finally {
            window.setTimeout(() => {
                copyButton.disabled = false;
            }, 300);
        }
    });
}
