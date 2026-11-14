# Guía de pruebas — app, agentes y RAG híbrido

**Fecha:** 14/11/2026 · **App en ejecución:** <http://localhost:5068> (Docker `ds-app`)
**SQL Server:** contenedor `ds-sql`, base `DocuSearch`, 9 expedientes, 3 usuarios.
**Embeddings:** `bge-m3` local en Ollama (`127.0.0.1:11434`), **89 páginas indexadas**, 1024 dimensiones.
**Motor generativo:** `local-extractive` (OpenCode no está escuchando en `:4097`); la **recuperación híbrida sí funciona** sin OpenCode.

> **Qué es el RAG de esta app.** Búsqueda **híbrida**: relevancia léxica + similitud coseno de embeddings locales por página, con RBAC y citas verificadas. Por defecto los vectores viven en SQL Server (`PageEmbeddings`) y `bge-m3` corre en Ollama local; **Pinecone es opcional** y solo recibe vectores y metadatos, nunca el texto. El modelo generativo, cuando está activo, recibe hasta 6 fragmentos de 1 000 caracteres.

---

## 1. Pruebas automáticas

### 1.1 Suites deterministas

```powershell
.\tests\run-all.ps1
```

| Suite | Qué cubre | Resultado esperado |
|---|---|---|
| Base | Datos, permisos, búsqueda léxica, PDF, TOTP, motores simulados | **87/87** |
| `--verifier` | AG-02: citas, páginas, fuentes, texto, duplicados | **97/97** |
| `--inspector` | AG-10: OCR, originales, metadatos, duplicados, integridad | **92/92** |
| `--catalog` | AG-01: parser y validación de ficha | **92/92** |
| `--audit` | AG-05: riesgos de la bitácora | **92/92** |
| `--auth` | Bloqueo de login por cuenta | **92/92** |
| `--rag` | Coseno, recuperación vectorial, híbrida y serialización | **92/92** |

### 1.2 Integración con SQL Server

Ya ejecutada en este entorno: **99/99**. Usa una base desechable `_tests`; no la base `DocuSearch`.

```powershell
$env:DOCUSEARCH_TEST_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch_tests;User Id=sa;Password=<SA_PASSWORD>;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8"
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --database
```

- `127.0.0.1,14331` es el proxy del SQL Server del contenedor.
- `<SA_PASSWORD>` está en `obj/_docker-run.ps1` (archivo local ignorado; no se versiona).
- La suite crea/restablece `DocuSearch_tests` y aplica la migración `PageEmbeddings`.

### 1.3 Integración con OpenCode / LangChain

Requiere OpenCode y el entorno Python; hoy no están activos:

```powershell
$env:OPENCODE_SERVER_PASSWORD = "DocuSearch-Local-2026"
opencode serve --port 4097 --hostname 0.0.0.0

$env:DOCUSEARCH_OPENCODE_URL = "http://127.0.0.1:4097"
$env:DOCUSEARCH_OPENCODE_PASSWORD = "DocuSearch-Local-2026"
$env:DOCUSEARCH_OPENCODE_PROVIDER = "deepseek"
$env:DOCUSEARCH_OPENCODE_MODEL = "deepseek-flash"
$env:DOCUSEARCH_PYTHON = "$env:LOCALAPPDATA\DocuSearch\LangChain\.venv\Scripts\python.exe"
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --langchain-opencode
```

- `--langchain-opencode` → **88/88** si el proveedor responde.
- `--opencode` prueba el modo directo.
- `--database --langchain` → **102/102** requiere además Ollama para chat (`:11434`).

---

## 2. Índice semántico

### 2.1 Estado actual

```powershell
docker exec ds-sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P <SA_PASSWORD> -C -d DocuSearch -W -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*), MIN(Dimensions), MAX(Dimensions) FROM dbo.PageEmbeddings;"
```

Debe devolver **89 · 1024 · 1024**.

### 2.2 Reindexar

Si vienes de una base sin `PageEmbeddings`, aplica la migración con
`dotnet run -- --initialize-database` (el contenedor demo la aplica al arrancar).
Después:

```powershell
# En el contenedor
docker exec ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --reindex-embeddings"

# En ejecución local
dotnet run -- --reindex-embeddings
```

La indexación también se ejecuta al **cargar, editar o reprocesar** un expediente. Si Ollama/`bge-m3` no está disponible, el documento sigue siendo buscable por el índice léxico.

### 2.3 Consulta de diagnóstico

```powershell
docker exec ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --search 'apoyo social depresión embarazadas violencia'"
```

Ejemplo esperado (verificado): devuelve `CNT-2026-89D4E4BF8F53` con puntuación híbrida y el aviso `Vector: bge-m3 (búsqueda híbrida)`.

### 2.4 Pinecone opcional

Con `Pinecone:Enabled=true` y `Pinecone__ApiKey`, los mismos vectores se sincronizan
al índice externo. Solo se envían vectores y metadatos, nunca texto.

```powershell
docker exec ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --sync-pinecone"
```

Debe informar cuántos vectores sincronizó. Después, `--search` muestra
`Pinecone: consultado.` Si Pinecone falla, la búsqueda local sigue funcionando.

---

## 3. Pruebas manuales de la app

### 3.1 Acceso

1. Abre <http://localhost:5068>.
2. Entra con tu correo autorizado y el código TOTP de 6 dígitos:
   - `acuyobl@gmail.com` → Administrador General.
   - `carloshm7@gmail.com` → Gestor Documental.
   - `hubertaf@gmail.com` → Auditor de Seguridad.
3. Comprueba la cabecera; **Salir** vuelve a `/login`.
4. `/enroll` devuelve 404 desde el host del contenedor (solo loopback); es lo esperado.

### 3.2 Recorrido de módulos

| Módulo | Ruta | Qué comprobar |
|---|---|---|
| Dashboard | `/` | KPIs, distribución, ranking |
| Documentos | `/management` | Catálogo, filtros, paginación, acciones por rol |
| Pipeline | `/pipeline` | Estados, SHA-256, reproceso, notas de OCR/embeddings |
| Buscador | `/search` | **Búsqueda híbrida** y coincidencia exacta; fuentes por página |
| Visor | desde Documentos | PDF, texto por página, resumen, entidades, notas |
| Reportes | `/reports` | CSV e informe HTML |
| Auditoría | `/audit` | Bitácora + **Resumen de riesgos AG-05** |
| Observabilidad | `/observability` | Trazas del agente; purga de trazas solo admin |
| Calidad | `/quality` | **Inspector AG-10** |
| Administración | `/rbac` | Usuarios, departamentos, RBAC, migración |
| Arquitectura | `/architecture` | Descripción del sistema |

---

## 4. Pruebas de los agentes

### 4.1 AG-02 — Verificador de citas

- Automático: `--verifier` (97/97).
- Con motor activo, pregunta por el contrato y verifica que la cita abra la página correcta; la traza de Observabilidad no guarda contenido.
- Si el motor devuelve citas inválidas o texto que no consta, AG-02 bloquea y cae al extractivo.

### 4.2 AG-01 — Catalogador (opt-in)

1. Documentos → Editar un expediente **Uso Interno** (`ACT-2026-5F5B59DB04D9`, `DIC-2026-4561EBFF5378`).
2. Sin consentimiento, **Sugerir ficha** debe rechazar.
3. Sin OpenCode activo, con consentimiento → aviso de motor no disponible.
4. Con OpenCode activo → propone ficha; no guarda sola; revisa y pulsa **Guardar cambios**.
5. El contrato `CNT-2026-EEAEE0BD49F8` es Confidencial: con motor remoto debe bloquear el envío; con modelo local sí se permite.

### 4.3 AG-05 — Auditor de riesgos

- `/audit` → **Resumen de riesgos** 7/30/90 días.
- Genera `LOGIN_FAILED`, `ACCESS_DENIED` o `INTEGRITY_MISMATCH` y comprueba contadores/alertas.

### 4.4 AG-10 — Inspector de calidad

- `/quality` → OCR pendiente, originales, metadatos, duplicados SHA-256 e integridad.
- Marca **Recalcular SHA-256** para verificar los PDF guardados.

---

## 5. Pruebas del RAG híbrido

### 5.1 Flujo

```text
Pregunta → RBAC → embedding de la consulta (bge-m3 local)
→ coseno contra PageEmbeddings + score léxico
→ hasta 6 fragmentos → motor (si está activo) → verificación AG-02 → citas
```

Si Ollama/embeddings no está disponible, la consulta sigue funcionando en modo léxico.

### 5.2 Consultas de prueba

| Consulta | Documento esperado | Resultado esperado |
|---|---|---|
| `compromiso de disponibilidad del proveedor` | `CNT-2026-EEAEE0BD49F8` | Contrato, p.1/p.2 (semántica) |
| `sanciones por retrasos en la entrega` | `CNT-2026-EEAEE0BD49F8` | Cláusula de SLA/penalizaciones |
| `Resume el expediente CNT-2026-EEAEE0BD49F8` | Contrato | Metadatos, resumen y fuentes |
| `¿Qué cubre el contrato de servicios 2026 y qué nivel de servicio garantiza?` | Contrato | SLA 99.99 % y cita a p.2 (con motor) |
| `¿Qué dice la página 2 del contrato de servicios 2026?` | Contrato | Texto de la página |
| `Extrae los montos del contrato de servicios 2026` | Contrato | Entidades MONEY |
| `Lista los documentos confidenciales` | Contrato | Listado filtrado por RBAC |
| `Resume el acta de junta directiva 2026` | `ACT-2026-5F5B59DB04D9` | Resumen y acuerdos |
| `¿Qué hallazgos tiene el informe de auditoría 2026?` | `DIC-2026-4561EBFF5378` | Hallazgos con fuentes |
| `¿Cuál es la capital de Francia?` | — | “No hay fuentes” / sin conocimiento externo |

### 5.3 Qué verificar

- Las consultas semánticas sin coincidencia literal deben recuperar la página correcta.
- Cada cita abre el visor en la página indicada.
- La puntuación mostrada es híbrida; en modo exacto se usa solo coincidencia textual.
- No se citan más de 6 fragmentos ni páginas inexistentes.
- Un documento no autorizado no aparece, aunque el vector sea similar.
- El texto de la página respalda la respuesta; AG-02 bloquea si no.

### 5.4 Qué no incluye

- Base vectorial externa obligatoria: no hay; Pinecone es opcional y SQL Server sigue siendo el índice local.
- OCR de escaneos: se marcan «Requiere OCR».
- Embeddings de proveedores remotos: `bge-m3` corre local.

---

## 6. Activar generación con OpenCode

El contenedor espera OpenCode en `host.docker.internal:4097` con password `DocuSearch-Local-2026`.

```powershell
$env:OPENCODE_SERVER_PASSWORD = "DocuSearch-Local-2026"
opencode serve --port 4097 --hostname 0.0.0.0
```

Luego entra como administrador y pulsa **Actualizar motores**. `/health` debe dejar de decir `local-extractive`. Los embeddings siguen siendo independientes de OpenCode.

---

## 7. Diagnóstico rápido

| Síntoma | Causa / acción |
|---|---|
| `health.assistant` = `local-extractive` | OpenCode apagado; la búsqueda híbrida sigue funcionando |
| `--search` dice “Vector: no disponible” | Ollama/bge-m3 no responde; revisa `Embeddings:BaseUrl` |
| Sin resultados semánticos | Índice vacío: ejecuta `--reindex-embeddings` |
| `PageEmbeddings` tiene 0 filas | Migración aplicada pero falta backfill |
| 429 en login | Rate limit 10/min por IP; espera un minuto |
| `/enroll` 404 | Normal en Docker: solo loopback |
| `/health/ready` 503 | SQL no disponible o migraciones pendientes |
| AG-01 dice “no hay motor generativo” | OpenCode apagado; los embeddings no bastan para generar |

---

## 8. Checklist de aceptación

- [ ] `tests\run-all.ps1` pasa (incluye `--rag` 92/92).
- [ ] `--database` pasa 99/99 con la migración `PageEmbeddings`.
- [ ] `PageEmbeddings` tiene 89 filas de 1024 dimensiones.
- [ ] `--search 'compromiso de disponibilidad del proveedor'` devuelve el contrato.
- [ ] Login TOTP y módulos `/quality`, `/audit`, `/observability`.
- [ ] Consultas semánticas del §5.2 devuelven documento/página correctos.
- [ ] Citas abren el visor en la página indicada.
- [ ] AG-01 rechaza sin consentimiento y no guarda solo.
- [ ] Contrato confidencial no se envía a motor remoto desde AG-01.
- [ ] `/observability` registra trazas sin contenido.
- [ ] Con OpenCode activo, `/health` deja de ser `local-extractive` y la generación usa el motor.
