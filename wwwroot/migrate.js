const statusElement = document.getElementById("status");
const exportButton = document.getElementById("export");
const metadataButton = document.getElementById("metadata");

function readArray(key) {
    const raw = localStorage.getItem(key);
    if (raw === null) throw new Error(`No se encontró ${key}. Comprueba que estás usando el origen y navegador anteriores.`);
    const value = JSON.parse(raw);
    if (!Array.isArray(value)) throw new Error(`El contenido de ${key} no es una lista válida.`);
    return value;
}

function openExistingFiles() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open("docusearch-files");
        request.onupgradeneeded = () => {
            request.transaction.abort();
            reject(new Error("No se encontró la base de PDF anterior. No se ha creado ni modificado ninguna base."));
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error("IndexedDB está bloqueada. Cierra las otras pestañas y vuelve a intentar."));
    });
}

function readPdf(db, id) {
    return new Promise((resolve, reject) => {
        const transaction = db.transaction("pdfs", "readonly");
        const request = transaction.objectStore("pdfs").get(id);
        transaction.oncomplete = () => resolve(request.result);
        transaction.onerror = () => reject(transaction.error);
        transaction.onabort = () => reject(transaction.error || new Error("Lectura de PDF cancelada."));
    });
}

function base64(blob) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(reader.result.split(",")[1]);
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(blob);
    });
}

async function exportBackup(includeFiles) {
    exportButton.disabled = true;
    metadataButton.disabled = true;
    let db;
    try {
        const documents = readArray("docusearch-store-documents");
        const backup = {
            schemaVersion: 1,
            exportedAt: new Date().toISOString(),
            documents: documents.map(({ pdfFileUrl, ...document }) => document),
            users: readArray("docusearch-store-active-users"),
            departments: readArray("docusearch-store-departments"),
            auditLogs: readArray("docusearch-store-audit-logs"),
            files: {}
        };
        if (documents.length > 100) throw new Error("El importador admite 100 expedientes por respaldo. No se ha exportado un respaldo incompleto.");
        if (includeFiles && documents.length) {
            db = await openExistingFiles();
            for (const document of documents) {
                statusElement.textContent = `Leyendo ${document.internalCode}…`;
                const blob = await readPdf(db, document.id);
                if (blob) backup.files[document.id] = await base64(blob);
            }
        }
        const blob = new Blob([JSON.stringify(backup)], { type: "application/json" });
        if (blob.size > 100 * 1024 * 1024) throw new Error("El respaldo supera los 100 MB admitidos por el importador. Conserva el navegador anterior para recuperar sus originales por lotes.");
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = `docusearch-respaldo-${Date.now()}.json`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
        statusElement.textContent = `Respaldo ${includeFiles ? "completo" : "SOLO DE METADATOS (sin archivos PDF)"} exportado: ${documents.length} expedientes y ${Object.keys(backup.files).length} PDF. Los documentos de ejemplo pueden no tener original. Verifica el respaldo antes de borrar datos.`;
    } catch (error) {
        console.error("No se pudo exportar el respaldo anterior.", error);
        statusElement.textContent = `Error: ${error.message}. No se han borrado datos.`;
    } finally {
        if (db) db.close();
        exportButton.disabled = false;
        metadataButton.disabled = false;
    }
}
exportButton.addEventListener("click", () => exportBackup(true));
metadataButton.addEventListener("click", () => exportBackup(false));
