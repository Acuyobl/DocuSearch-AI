window.docusearch = {
    scrollToEnd(element) {
        element.scrollTop = element.scrollHeight;
    },
    openDialog(target) {
        const element = typeof target === "string" ? document.getElementById(target) : target;
        if (!element || element.open) return;
        if (!element._lightDismiss) {
            element._lightDismiss = (event) => {
                if (event.target === element) element.close();
            };
            element.addEventListener("click", element._lightDismiss);
        }
        element.showModal();
    },
    closeDialog(target) {
        const element = typeof target === "string" ? document.getElementById(target) : target;
        if (element && element.open) element.close();
    },
    focusById(id) {
        const element = document.getElementById(id);
        if (element) element.focus();
    },
    toggleTheme() {
        const theme = document.documentElement.dataset.theme === "light" ? "dark" : "light";
        document.documentElement.dataset.theme = theme;
        const meta = document.querySelector('meta[name="color-scheme"]');
        if (meta) meta.content = theme;
        try {
            localStorage.setItem("docusearch-theme", theme);
        } catch (error) {
            console.warn("No se pudo guardar el tema del navegador.", error);
        }
    },
    palettes: ["original", "esmeralda"],
    /** Aplica y recuerda la paleta; cualquier valor desconocido vuelve a «original». */
    setPalette(name) {
        const palette = this.palettes.includes(name) ? name : "original";
        if (palette === "original") delete document.documentElement.dataset.palette;
        else document.documentElement.dataset.palette = palette;
        try {
            localStorage.setItem("docusearch-palette", palette);
        } catch (error) {
            console.warn("No se pudo guardar la paleta del navegador.", error);
        }
        return palette;
    },
    getPalette() {
        return document.documentElement.dataset.palette || "original";
    },
    async blobUrl(stream, type) {
        return URL.createObjectURL(new Blob([await stream.arrayBuffer()], { type }));
    },
    revokeUrl(url) {
        if (url) URL.revokeObjectURL(url);
    },
    async download(stream, filename, type) {
        const url = await this.blobUrl(stream, type);
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },
    downloadBase64(base64, filename, type) {
        const binary = atob(base64);
        const bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
        const url = URL.createObjectURL(new Blob([bytes], { type }));
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },
    print() {
        window.print();
    },
    confirm(message) {
        return window.confirm(message);
    },
    bindChatComposer(element, dotNetRef) {
        if (!element) return;
        if (element._chatComposerHandler) element.removeEventListener("keydown", element._chatComposerHandler);
        const handler = (event) => {
            if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) return;
            event.preventDefault();
            dotNetRef.invokeMethodAsync("SendFromEnter").catch(() => {});
        };
        element._chatComposerHandler = handler;
        element.addEventListener("keydown", handler);
    },
    unbindChatComposer(element) {
        if (!element) return;
        if (element._chatComposerHandler) element.removeEventListener("keydown", element._chatComposerHandler);
        delete element._chatComposerHandler;
    }
};
try {
    document.documentElement.dataset.theme = localStorage.getItem("docusearch-theme") || "dark";
} catch (error) {
    document.documentElement.dataset.theme = "dark";
    console.warn("No se pudo recuperar el tema del navegador.", error);
}
try {
    const palette = localStorage.getItem("docusearch-palette");
    if (palette && palette !== "original" && window.docusearch.palettes.includes(palette))
        document.documentElement.dataset.palette = palette;
} catch (error) {
    console.warn("No se pudo recuperar la paleta del navegador.", error);
}
