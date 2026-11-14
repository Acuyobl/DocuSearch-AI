# Guía de pruebas — app, agentes y RAG léxico

**Fecha:** 14/11/2026 · **App en ejecución:** <http://localhost:5068> (Docker `ds-app`)
**Base de datos:** SQL Server del contenedor `ds-sql`, base `DocuSearch`, 9 expedientes, 3 usuarios.
**Estado del motor:** `local-extractive` porque OpenCode no está escuchando en `:4097`.

> **Aclaración importante sobre “RAG”.** DocuSearch AI **no usa embeddings ni base vectorial**. Su recuperación es **léxica** (términos normalizados, sin similitud semántica) y el modelo, cuando está activo, recibe hasta **6 fragmentos de 1 000 caracteres**. Es un RAG *léxico con citas verificadas*, no un RAG vectorial. Lo que se prueba abajo es ese flujo real.

---

## 1. Pruebas automáticas

### 1.1 Suites deterministas (sin servicios)

Desde la raíz del repositorio:

```powershell
.\tests\run-all.ps1
```

Ejecuta, en orden:

| Suite | Qué cubre | Resultado esperado |
|---|---|---|
| Base | Datos, permisos, búsqueda, PDF, TOTP, OpenCode/Ollama simulados | **87/87** |
| `--verifier` | AG-02: citas, páginas, fuentes, texto y duplicados | **97/97** |
| `--inspector` | AG-10: OCR, originales, metadatos, duplicados e integridad | **92/92** |
| `--catalog` | AG-01: parser y validación de ficha | **92/92** |
| `--audit` | AG-05: riesgos de la bitácora | **92/92** |
| `--auth` | Bloqueo de login por cuenta | **92/92** |

También se pueden ejecutar por separado:

```powershell
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --verifier
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --inspector
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --catalog
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --audit
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --auth
```

### 1.2 Integración con SQL Server real

En este entorno ya se ejecutó y dio **99/99**. Usa una base desechable terminada en `_tests`; **nunca** la base `DocuSearch`.

```powershell
$env:DOCUSEARCH_TEST_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch_tests;User Id=sa;Password=<SA_PASSWORD>;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8"
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --database
```

- `127.0.0.1,14331` es el proxy del SQL Server del contenedor.
- `<SA_PASSWORD>` está en `obj/_docker-run.ps1` (archivo local ignorado por Git; no se versiona).
- La suite crea/restablece `DocuSearch_tests`; no toca `DocuSearch`.
- También: `.\tests\run-all.ps1 -IncludeDatabase` con la variable definida.

### 1.3 Integración con OpenCode / LangChain (cuando el motor esté activo)

Requiere un servidor OpenCode y el entorno Python. En este entorno **no están activos**, por eso quedan pendientes:

```powershell
# Servidor OpenCode (host; usar el password configurado en el contenedor)
$env:OPENCODE_SERVER_PASSWORD = "DocuSearch-Local-2026"
opencode serve --port 4097 --hostname 0.0.0.0

# Prueba C# con LangChain + OpenCode
$env:DOCUSEARCH_OPENCODE_URL = "http://127.0.0.1:4097"
$env:DOCUSEARCH_OPENCODE_PASSWORD = "DocuSearch-Local-2026"
$env:DOCUSEARCH_OPENCODE_PROVIDER = "deepseek"
$env:DOCUSEARCH_OPENCODE_MODEL = "deepseek-flash"
$env:DOCUSEARCH_PYTHON = "$env:LOCALAPPDATA\DocuSearch\LangChain\.venv\Scripts\python.exe"
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --langchain-opencode
```

- `--langchain-opencode` → **88/88** (si el proveedor/modelo responde).
- `--opencode` prueba el modo directo contra el servidor real.
- `--database --langchain` → **102/102** requiere además Ollama en `:11434` (hoy no está activo).

---

## 2. Pruebas manuales de la app

### 2.1 Acceso

1. Abre <http://localhost:5068>.
2. Entra con el correo autorizado y el código de 6 dígitos de la app autenticadora:
   - `acuyobl@gmail.com` → Administrador General.
   - `carloshm7@gmail.com` → Gestor Documental.
   - `hubertaf@gmail.com` → Auditor de Seguridad.
3. Comprueba la cabecera con el usuario y el rol; **Salir** vuelve a `/login`.
4. `/enroll` devuelve 404 desde el host del contenedor (solo loopback); es lo esperado. Los dispositivos ya están inscritos.

### 2.2 Recorrido de módulos

| Módulo | Ruta | Qué comprobar |
|---|---|---|
| Dashboard | `/` | KPIs, distribución, ranking; sin datos inventados |
| Documentos | `/management` | Catálogo, filtros, paginación, acciones según rol |
| Pipeline | `/pipeline` | Estados, SHA-256, reproceso |
| Buscador + Asistente | `/search` | Búsqueda exacta y por términos; pestaña Asistente IA |
| Visor | desde Documentos | PDF, texto por página, resumen, entidades, notas |
| Reportes | `/reports` | CSV e informe HTML |
| Auditoría | `/audit` | Bitácora + **Resumen de riesgos AG-05** |
| Observabilidad | `/observability` | Trazas de las últimas 500 consultas; botón de purga solo admin |
| Calidad | `/quality` | **Inspector AG-10**: OCR, originales, metadatos, duplicados, integridad |
| Administración | `/rbac` | Usuarios, departamentos, matriz RBAC, migración |
| Arquitectura | `/architecture` | Descripción del sistema |

---

## 3. Pruebas de los agentes

### 3.1 AG-02 — Verificador de citas

**Automático:** `--verifier` (97/97).

**Manual (con motor generativo activo):**
1. Activa OpenCode y refresca motores (ver §5).
2. Pregunta: `¿Qué cubre el contrato de servicios 2026 y qué nivel de servicio garantiza?`.
3. Comprueba que la respuesta incluye una **cita clicable** (`CNT-2026-EEAEE0BD49F8 · p. 2`) y que al abrirla se ve el texto del SLA.
4. En **Observabilidad**, revisa la traza: motor, modelo, duración, tokens, estado; **sin pregunta ni respuesta**.
5. Si el motor devuelve citas inválidas o texto que no consta, AG-02 bloquea y cae al extractivo con la etiqueta “respuesta bloqueada por verificación”. Esto se cubre en la suite automática; forzarlo en vivo requiere manipular el motor.

### 3.2 AG-01 — Catalogador (opt-in)

1. Abre **Documentos → Editar** un expediente **Uso Interno** (por ejemplo `ACT-2026-5F5B59DB04D9` o `DIC-2026-4561EBFF5378`).
2. Sin marcar la casilla de consentimiento, pulsa **Sugerir ficha** → debe rechazar con un mensaje claro.
3. Sin OpenCode activo, con consentimiento → debe decir que no hay motor generativo activo.
4. Con OpenCode activo y consentimiento → rellena título, categoría, departamento, confidencialidad, palabras clave y resumen. **No guarda nada**: revisa y pulsa **Guardar cambios**.
5. Prueba el contrato `CNT-2026-EEAEE0BD49F8` (Confidencial) con motor remoto: debe **bloquear** el envío por confidencialidad. Con un modelo local sí se permite.
6. Comprueba que la traza del Catalogador aparece en Observabilidad sin contenido.

### 3.3 AG-05 — Auditor de riesgos

1. Abre `/audit`.
2. Revisa **Resumen de riesgos** con periodo 7/30/90 días: intentos fallidos, accesos denegados, integridad, cambios administrativos, descargas y acciones frecuentes.
3. Genera eventos:
   - Cierra sesión e intenta entrar con un código TOTP incorrecto varias veces (máximo 10; luego el bloqueo por cuenta se activa).
   - Abre un expediente sin permiso con un usuario restringido o fuerza un `ACCESS_DENIED` desde Administración.
4. Actualiza `/audit`: los contadores y alertas deben subir; la bitácora no guarda contenido de documentos.

### 3.4 AG-10 — Inspector de calidad

1. Abre `/quality`.
2. Pulsa **Actualizar informe**. Debe mostrar: expedientes revisados, OCR pendiente, sin original, metadatos incompletos, duplicados SHA-256 e integridad.
3. Marca **Recalcular SHA-256 de los originales** y vuelve a ejecutar: tarda más y puede reportar `INTEGRITY_MISMATCH`.
4. Comprueba que solo administración general y auditoría acceden al módulo.
5. El informe no muestra texto extraído ni permite modificar documentos.

---

## 4. Pruebas del “RAG” léxico

### 4.1 Flujo que se está probando

```text
Pregunta → RBAC → LocalSearch léxico (sin embeddings)
→ hasta 6 fragmentos de 1 000 caracteres → motor (si está activo)
→ JSON answer + citations → verificación C# → respuesta con citas
```

Si no hay motor: respuesta **extractiva** con las mismas fuentes. No hay base vectorial ni OCR.

### 4.2 Consultas de prueba (datos reales del contenedor)

| Consulta | Documento esperado | Resultado esperado |
|---|---|---|
| `Resume el expediente CNT-2026-EEAEE0BD49F8` | Contrato de servicios 2026 | Metadatos, resumen y fuentes |
| `¿Qué cubre el contrato de servicios 2026 y qué nivel de servicio garantiza?` | `CNT-2026-EEAEE0BD49F8` | SLA 99.99 %, tiempos de respuesta y cita a p. 2 (con motor) |
| `¿Qué dice la página 2 del contrato de servicios 2026?` | `CNT-2026-EEAEE0BD49F8` | Texto de la página 2, sin inventar |
| `Extrae los montos del contrato de servicios 2026` | `CNT-2026-EEAEE0BD49F8` | Entidades `MONEY` con página |
| `Lista los documentos confidenciales` | `CNT-2026-EEAEE0BD49F8` | Listado filtrado por RBAC |
| `Busca "mantenimiento preventivo y correctivo"` | Contrato | Fragmentos con cita |
| `¿Y su vigencia?` (después del contrato) | `CNT-2026-EEAEE0BD49F8` | Seguimiento del último expediente |
| `Resume el acta de junta directiva 2026` | `ACT-2026-5F5B59DB04D9` | Resumen y acuerdos |
| `¿Qué hallazgos tiene el informe de auditoría 2026?` | `DIC-2026-4561EBFF5378` | Hallazgos con fuentes |
| `¿Cuál es la capital de Francia?` | — | “No hay fuentes…” / no usa conocimiento externo |

### 4.3 Qué verificar en cada respuesta

- **Citas:** cada chip debe abrir el visor en la página indicada.
- **Evidencia:** el texto de la página debe contener lo afirmado; si no, AG-02 debe bloquear con motor activo.
- **Límite de contexto:** no debe citar más de 6 fragmentos ni inventar páginas.
- **Sin motor:** la respuesta se marca como extractiva y conserva las fuentes.
- **RBAC:** el contrato es Confidencial; un perfil sin nivel suficiente no debe verlo.
- **Seguimiento:** `¿Y su vigencia?` mantiene el último código citado, no memoria completa.

### 4.4 Qué NO se puede probar (por diseño)

- Similitud semántica o sinónimos por embeddings: no existen.
- OCR de páginas escaneadas: se marca “Requiere OCR”; no se inventa.
- Documento completo en el prompt: solo 6 × 1 000 caracteres.
- Herramientas del modelo, SQL, archivos o internet: desactivadas.

---

## 5. Activar el motor generativo para probar agentes con LLM

El contenedor espera OpenCode en `host.docker.internal:4097` con password `DocuSearch-Local-2026`.

```powershell
$env:OPENCODE_SERVER_PASSWORD = "DocuSearch-Local-2026"
opencode serve --port 4097 --hostname 0.0.0.0
```

Después, en la app:
1. Entra como Administrador General.
2. En el asistente, pulsa **Actualizar motores**.
3. Comprueba `http://localhost:5068/health`: `assistant` debe dejar de ser `local-extractive`.
4. Si el proveedor no tiene cuota, el asistente cae al extractivo; no cambia de proveedor solo.

> Si ejecutás la app localmente (`dotnet run`) en lugar de Docker, OpenCode puede escuchar en `127.0.0.1:4097`.

---

## 6. Diagnóstico rápido

| Síntoma | Causa / acción |
|---|---|
| `/health` = `local-extractive` | OpenCode no está activo o no cargó el catálogo; inicia el servidor y pulsa **Actualizar motores** |
| AG-01 dice “no hay motor generativo” | Igual que arriba; el Catalogador no inventa una ficha sin motor |
| Login devuelve “código incorrecto” | Revisa la hora del equipo y el código de 30 s; tras 10 fallos hay bloqueo temporal |
| 429 en login | Rate limit de 10/min por IP; espera un minuto |
| `/enroll` 404 | Normal en Docker: solo responde en loopback del servidor |
| `/health/ready` 503 | SQL no disponible o migraciones pendientes; en Docker revisa `docker ps` y `docker logs ds-app` |
| `/quality` 403/redirección | El rol no es administración general ni auditoría |
| El asistente responde sin IA | Es el modo extractivo: fuentes y citas, sin modelo generativo |

Comandos útiles:

```powershell
docker ps --filter name=ds-app
docker logs -f ds-app
docker logs --tail 100 ds-sql
curl.exe -s http://127.0.0.1:5068/health
```

---

## 7. Checklist de aceptación

- [ ] `tests\run-all.ps1` termina sin fallos.
- [ ] `--database` termina en **99/99** contra `DocuSearch_tests`.
- [ ] Login TOTP con administrador y gestor.
- [ ] `/quality` y `/audit` responden 200 para admin/auditoría.
- [ ] Las consultas de §4.2 devuelven fuentes y citas coherentes.
- [ ] Abrir una cita lleva a la página correcta del visor.
- [ ] AG-01 rechaza sin consentimiento y no guarda solo.
- [ ] El contrato confidencial no se envía a un motor remoto desde AG-01.
- [ ] `/observability` registra motor, modelo, duración y tokens; sin contenido.
- [ ] Con OpenCode activo, `/health` deja de ser `local-extractive` y AG-01 propone ficha en un documento Uso Interno.
