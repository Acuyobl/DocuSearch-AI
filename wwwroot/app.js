window.docusearch = {
    scrollToEnd(element) {
        element.scrollTop = element.scrollHeight;
    },
    toggleTheme() {
        const theme = document.documentElement.dataset.theme === "light" ? "dark" : "light";
        document.documentElement.dataset.theme = theme;
        try {
            localStorage.setItem("docusearch-theme", theme);
        } catch (error) {
            console.warn("No se pudo guardar el tema del navegador.", error);
        }
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
    print() {
        window.print();
    },
    confirm(message) {
        return window.confirm(message);
    }
};
try {
    document.documentElement.dataset.theme = localStorage.getItem("docusearch-theme") || "dark";
} catch (error) {
    document.documentElement.dataset.theme = "dark";
    console.warn("No se pudo recuperar el tema del navegador.", error);
}
