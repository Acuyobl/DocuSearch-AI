# Guía de mejoras — DocuSearch AI

**Fecha:** 28/11/2026 · **Base:** commit `40897c3` · **Método:** grafo de código con graphify (2.287 nodos, 5.044 aristas, 129 comunidades, sin ciclos de importación) + revisión manual de los archivos con más conexiones.

Esta guía complementa, sin repetirlos, [`DE-DEMO-A-PRODUCCION.md`](../DE-DEMO-A-PRODUCCION.md) (hoja de ruta de producción) y [`ESTADO-IMPLEMENTACION.md`](../ESTADO-IMPLEMENTACION.md) (lo ya hecho). Aquí solo aparecen debilidades **verificadas en el código actual**, con el cambio propuesto y cómo implementarlo.

---

## 1. Resumen

El proyecto está bien protegido en el perímetro: `ProductionGuard`, TOTP con anti-replay, bitácora de solo inserción, doble control, presupuesto de IA y verificador de citas. Los riesgos principales están en **rendimiento y escalabilidad**: cada pregunta al asistente carga en memoria todo el corpus legible, y el almacén vectorial SQL compara contra todos los fragmentos. Con pocas decenas de documentos no se nota; con unos miles, la memoria por usuario y la latencia crecen de forma lineal.

| # | Debilidad | Impacto | Esfuerzo | Prioridad |
|---|---|---|---|---|
| D1 | Cada consulta RAG carga todo el corpus (dos veces) | Alto | Medio | **P0** |
| D2 | Búsqueda vectorial SQL por fuerza bruta en C# | Alto | Medio | **P0** |
| D3 | `DocumentItem.Pages` deserializa JSON en cada acceso | Medio | Bajo | **P0** |
| D4 | Datos personales reales versionados | Medio | Bajo | **P0** |
| D5 | Estado crítico de seguridad en memoria del proceso | Alto en multi-instancia | Medio | P1 |
| D6 | Autorización manual repetida, sin políticas | Medio | Bajo | P1 |
| D7 | `Program.cs` monolítico (858 líneas) | Medio | Bajo | P1 |
| D8 | Opciones sin validar al arrancar | Medio | Bajo | P1 |
| D9 | `/health` público revela el modelo de IA | Bajo | Bajo | P1 |
| D10 | Suite de pruebas casera y CI parcial | Medio | Medio | P2 |
| D11 | Worker Python lanzado como proceso por petición | Medio | Medio | P2 |
| D12 | Raíz del repositorio saturada de documentos y binarios | Bajo | Bajo | P2 |

---

## 2. Debilidades y cambios

### D1 — Cada consulta RAG carga todo el corpus (P0)

**Evidencia**
- [`DocumentService.LoadAsync`](../Services/DocumentService.cs#L74) devuelve **todos** los documentos legibles con su texto completo (`PagesJson`), **todos** los `PageEmbeddings` (1024 floats por página), todos los usuarios, todos los departamentos y 1.000 eventos de auditoría.
- [`RagQueryService`](../Services/Rag/RagQueryService.cs#L99) lo llama en la línea 99 y otra vez en la [268](../Services/Rag/RagQueryService.cs#L268): **dos cargas completas por pregunta**.
- `CatalogSuggester` y `WorkspaceState` hacen lo mismo; `WorkspaceState` retiene el resultado **por circuito Blazor**, o sea, una copia del corpus por pestaña abierta.

**Cambio**
Separar la «instantánea de pantalla» de la «consulta de permisos»:

1. Nuevo método `DocumentService.ReadableIdsAsync()` que devuelve solo `Id`, `InternalCode`, `UploadDate` (proyección, sin `PagesJson`).
2. `RagQueryService` usa esos IDs para filtrar el retriever y carga el texto **solo de los documentos ganadores** (`LoadByIdsAsync(ids)`).
3. Eliminar la segunda llamada de la línea 268; reutilizar el resultado de la primera.
4. `LoadAsync` deja de adjuntar `PageVectors`; la búsqueda híbrida de [`LocalSearch`](../Services/LocalSearch.cs) pide los vectores al almacén, no al snapshot.
5. Auditoría, usuarios y departamentos se cargan bajo demanda en sus módulos, no en cada snapshot.

**Criterio de aceptación:** con 1.000 documentos de 20 páginas, una pregunta no materializa más de `TopK` documentos completos (medir con el `Stopwatch` que ya registra `AgentTrace.DurationMs`).

### D2 — Búsqueda vectorial SQL por fuerza bruta (P0)

**Evidencia:** [`SqlRagVectorStore.QueryAsync`](../Services/Rag/SqlRagVectorStore.cs#L55) trae a memoria todos los fragmentos de los documentos legibles (texto **y** vector), y calcula el coseno en C# con `double`.

**Cambio (elegir una opción)**

| Opción | Cuándo | Cómo |
|---|---|---|
| A. Tipo `VECTOR` nativo | SQL Server 2025 / Azure SQL disponible | Columna `VECTOR(1024)` y `VECTOR_DISTANCE('cosine', …)` con `TOP (@k)` en SQL; el texto se lee solo para los `k` ganadores. |
| B. Caché de vectores en memoria | SQL Server 2019/2022 (la base usa nivel 150) | Singleton `RagVectorCache` con `float[]` normalizados por `ChunkId`, invalidado en `ReplaceDocumentAsync`/`DeleteDocumentAsync`. Con vectores normalizados el coseno es un producto escalar (`System.Numerics.Tensors.TensorPrimitives.Dot`). Texto solo para los ganadores. |
| C. Pinecone por defecto | Volumen > ~100k fragmentos | Ya existe `PineconeRagVectorStore`; activar `Rag:VectorStore=pinecone` y dejar SQL como respaldo. |

Recomendación: **B ahora** (no cambia la infraestructura) y **A** cuando se migre el servidor.

### D3 — `Pages` deserializa en cada acceso (P0)

**Evidencia:** el getter de [`DocumentItem.Pages`](../Models/Domain.cs#L49) ejecuta `JsonSerializer.Deserialize` cada vez; hay 34 accesos en servicios y componentes, varios dentro de bucles de búsqueda. `Notes` sigue el mismo patrón.

**Cambio:** caché perezosa invalidada en el setter.

```csharp
private List<DocumentPage>? pagesCache;
[NotMapped] public List<DocumentPage> Pages
{
    get => pagesCache ??= JsonSerializer.Deserialize<List<DocumentPage>>(PagesJson, Catalog.Json)
        ?? throw new InvalidDataException("Páginas almacenadas inválidas.");
    set { pagesCache = value; PagesJson = JsonSerializer.Serialize(value, Catalog.Json); }
}
```

Atención: si algún código modifica la lista devuelta sin reasignarla, ese cambio ya no se persistiría. Revisar los 34 usos (`grep -rn "\.Pages\b"`) antes de aplicar.

### D4 — Datos personales reales versionados (P0)

**Evidencia:** correos reales de personas en [`appsettings.json`](../appsettings.json) (`Auth:Users`), en `Data/seed.json` y en `tests/Program.cs`; además, el nombre de la máquina en `ConnectionStrings:Documents`. El CI ya bloquea secretos TOTP, pero no correos.

**Cambio**
1. Sustituir en el repositorio por cuentas `@example.test` (ya existe `consultor.demo@example.test` como modelo).
2. Las cuentas reales viven en `appsettings.Local.json` (ya está en `.gitignore`) o en `dotnet user-secrets`.
3. Cadena de conexión por defecto `Server=localhost\\SQLEXPRESS`.
4. Añadir al CI un paso que falle si aparece `@gmail.com` (o cualquier dominio que no sea `example.test`) en archivos versionados.
5. Valorar reescribir el historial (`git filter-repo`) si el repositorio llega a ser público. Esto requiere coordinarse con todos los que tengan clones.

### D5 — Estado de seguridad en memoria del proceso (P1)

**Evidencia:** viven en memoria del proceso y se pierden al reiniciar o no se comparten entre instancias:
- anti-replay TOTP en `IMemoryCache` ([`AuthService`](../Services/AuthService.cs#L120)): con dos instancias, el mismo código vale una vez en cada una;
- bloqueo por cuenta ([`LoginAttemptLimiter`](../Services/LoginAttemptLimiter.cs)) y rate limit de login;
- conversación del asistente ([`AssistantMemory`](../Services/AssistantMemory.cs)), que caduca a las 24 h;
- circuit breakers ([`AgentResilience`](../Services/AgentResilience.cs)).

**Cambio**
- Corto plazo: documentar «**una sola instancia**» como requisito en `ProductionGuard` (advertencia si se detecta una configuración de granja).
- **No** fijar `SizeLimit` en `AddMemoryCache`: todas las entradas ya caducan, y con un límite global una inundación de entradas (p. ej. conversaciones) impediría registrar el anti-replay TOTP o el bloqueo de cuenta, que comparten esa caché.
- Antes de escalar: `IDistributedCache` sobre SQL Server (`Microsoft.Extensions.Caching.SqlServer`, sin infraestructura nueva) para el anti-replay TOTP y el bloqueo por cuenta. Blazor Server necesita además sesiones *sticky* en el balanceador.

### D6 — Autorización manual repetida (P1)

**Evidencia:** los endpoints `/api/mcp/tokens` (GET/POST/DELETE) en [`Program.cs`](../Program.cs#L806) repiten el mismo bloque: comprobar autenticación, `session.SignIn(context.User)`, cargar el actor y comprobar `Permissions.CanAdmin`. No hay ninguna política de autorización (`AddPolicy`) ni `RequireRole`; olvidar una línea abre un endpoint.

**Cambio**
1. Políticas en `AddAuthorization`: `Admin`, `Auditor`, `Gestor`, que reutilizan `Permissions.*` mediante un `IAuthorizationHandler` que lee el usuario **activo** de la base (así un usuario desactivado pierde el acceso aunque conserve la cookie).
2. Un `IEndpointFilter` `HydrateSession` que hace `session.SignIn(context.User)` una sola vez.
3. Agrupar: `app.MapGroup("/api/mcp/tokens").RequireAuthorization("Admin").AddEndpointFilter<HydrateSession>()`.
4. Aplicar el mismo patrón en `MapGovernanceApi`.

### D7 — `Program.cs` monolítico (P1)

**Evidencia:** 858 líneas. Nueve comandos de consola (`--initialize-database`, `--rag-eval`, `--search`, …) con el mismo patrón copiado de «suplantar al primer administrador activo», middleware de cabeceras, login, MCP y endpoints, todo en línea.

**Cambio** (refactor sin cambio de comportamiento):

```
Hosting/
  ServiceRegistration.cs   // AddDocuSearchCore(), AddDocuSearchRag(), AddDocuSearchAi()
  SecurityMiddleware.cs    // cabeceras, LocalOnly, X-Request-Id, puerta de autenticación
  AuthEndpoints.cs         // /login, /account/*, /enroll
  HealthEndpoints.cs
  ApiEndpoints.cs          // /api/rag, /api/mcp/tokens, /assistant/engines
Cli/
  CliCommands.cs           // diccionario comando → handler; helper RunAsAdminAsync()
```

`Program.cs` queda en ~60 líneas. Hacerlo **después de D6** para mover ya el código limpio.

### D8 — Opciones sin validar al arrancar (P1)

**Evidencia:** ninguna sección usa `ValidateDataAnnotations()` ni `ValidateOnStart()`. Un `Rag:TopK = 0` o un `Pinecone:Dimension` distinto de `Embeddings:Dimensions` solo falla en la primera consulta (o degrada en silencio: `SqlRagVectorStore` descarta los fragmentos incompatibles con una simple advertencia).

**Cambio:** `builder.Services.AddOptions<RagOptions>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` para `Rag`, `Embeddings`, `Pinecone`, `Auth`, `Resilience` y `AiGovernance`, con `[Range]` en los campos y una regla cruzada `Pinecone.Dimension == Embeddings.Dimensions` (`IValidateOptions<>`).

### D9 — `/health` público revela el modelo de IA (P1)

**Evidencia:** [`/health`](../Program.cs#L724) es anónimo y devuelve `assistant` (proveedor y modelo) y `profile`.

**Cambio:** `/health` anónimo devuelve solo `status`; el detalle pasa a `/assistant/engines`, que ya exige sesión, o a una variante protegida por la política `Admin`.

### D10 — Suite de pruebas casera y CI parcial (P2)

**Evidencia**
- [`tests/Program.cs`](../tests/Program.cs) tiene 2.374 líneas con ~208 comprobaciones sobre un mini-framework propio (`Check`, `Assert`): no permite filtrar una prueba suelta, no mide cobertura ni ejecuta en paralelo.
- El CI ejecuta `dotnet run` 11 veces (11 compilaciones incrementales y 11 arranques).
- No se ejecutan en CI: la suite `--database`, los tests Python (`LangChain/test_*.py`, `tests/mcp/`) ni Playwright (`tests/e2e/`).

**Cambio**
1. Inmediato: un modo `--all` que agrupe los 11 conjuntos en una sola invocación.
2. Trabajo de CI con SQL Server: job `ubuntu-latest` con servicio `mcr.microsoft.com/mssql/server:2022-latest` y `DOCUSEARCH_TEST_CONNECTION` con autenticación SQL (la suite ya exige el sufijo `_tests`).
3. Job Python: `pytest LangChain/` (sin red).
4. Migración gradual a xUnit: proyecto `tests/DocuSearch.Tests` nuevo; cada vez que se toque un grupo de checks se mueve a xUnit. No hace falta reescribirlo todo de golpe.
5. Cobertura con `coverlet.collector` y publicación del informe como artefacto.

### D11 — Worker Python como proceso por petición (P2)

**Evidencia:** [`LangChainRunner.RunAsync`](../Services/LangChainRunner.cs#L10) lanza `python.exe` con `ProcessStartInfo` en cada llamada (LangGraph y LangChain). Cada pregunta paga el arranque del intérprete y la importación de LangChain (varios segundos).

**Cambio:** si `Assistant:UseLangGraph` o `OpenCode:UseLangChain` van a usarse en producción, convertir el worker en un servicio residente (FastAPI/uvicorn en loopback) con `HttpClient` tipado y `AgentResilience`. Si son experimentales, mantenerlos apagados (como ahora) y documentarlo así.

### D12 — Raíz del repositorio saturada (P2)

**Evidencia:** 25 archivos `.md` en la raíz, PDF/DOCX/PNG versionados (~5,7 MB), tres archivos `Prompt*.txt` (uno con espacio antes de la extensión) y duplicados (`ANALISIS_PROYECTO.md` se titula «Estado de implementación», igual que `ESTADO-IMPLEMENTACION.md`).

**Cambio**
```
docs/
  arquitectura/   (Architecture, LANGGRAPH-*, MCP-*, RBAC-ACCESO, MULTIAGENTES-ANALISIS)
  operacion/      (GUIA-PRUEBAS, MCP-PRUEBAS, PRODUCCION-CAMBIOS, DE-DEMO-A-PRODUCCION)
  estado/         (ESTADO-IMPLEMENTACION, RAG-AUDITORIA, REVISION-COMPONENTES)
  archivo/        (prompts históricos, COMPARACION-*, PRESENTACION)
```
Los PDF se regeneran desde el Markdown y no se versionan (o se publican como *release assets*). Fusionar `ANALISIS_PROYECTO.md` con `ESTADO-IMPLEMENTACION.md`. Mantener en la raíz solo `README`, `CONTRIBUTING`, `SECURITY` y `LICENSE`.

---

## 3. Guía de implementación

Orden pensado para que cada paso sea un PR pequeño, revisable y reversible. Cada PR debe pasar el CI actual sin cambios en los checks existentes, salvo que el PR los amplíe.

### Fase 1 — Higiene y riesgos baratos (≈ 2 días)

| PR | Contenido | Debilidad | Verificación |
|---|---|---|---|
| 1 | Correos `@example.test`, cadena de conexión genérica y paso de CI anti-PII | D4 | CI rojo si se reintroduce un correo real |
| 2 | Caché perezosa en `Pages`/`Notes` + auditoría de los 34 usos | D3 | `--rag` y `--database` en verde |
| 3 | `/health` mínimo para anónimos | D9 | `curl /health` sin modelo |
| 4 | `--all` en los checks; CI con una sola invocación | D10.1 | Tiempo de CI antes y después |

### Fase 2 — Rendimiento del RAG (≈ 1 semana)

| PR | Contenido | Debilidad | Verificación |
|---|---|---|---|
| 5 | `ReadableIdsAsync` + `LoadByIdsAsync`; `RagQueryService` sin doble carga | D1 | Nuevo check: una consulta materializa ≤ `TopK` documentos |
| 6 | `RagVectorCache` (opción B) con invalidación en ingesta y borrado | D2 | `--rag-eval` con métricas iguales o mejores; latencia p95 registrada en `AgentTraces` |
| 7 | `LoadAsync` sin `PageVectors`, auditoría y usuarios bajo demanda | D1 | Memoria por circuito (dotnet-counters) antes y después |

**Antes del PR 5**, guardar una línea base: `dotnet run -- --rag-eval` y la latencia media de `AgentTraces` de una semana. Sin esos números no se puede demostrar la mejora.

### Fase 3 — Estructura y seguridad (≈ 1 semana)

| PR | Contenido | Debilidad |
|---|---|---|
| 8 | Políticas `Admin`/`Auditor`/`Gestor` + filtro `HydrateSession`; aplicar a `/api/*` | D6 |
| 9 | Validación de opciones con `ValidateOnStart` y regla cruzada de dimensiones | D8 |
| 10 | Extraer `Hosting/` y `Cli/` de `Program.cs` (solo mover código) | D7 |
| 11 | Reorganización de `docs/` | D12 |

### Fase 4 — Escala y calidad continua (cuando haya decisión de despliegue)

| PR | Contenido | Debilidad |
|---|---|---|
| 12 | `IDistributedCache` SQL para anti-replay TOTP y bloqueo de cuenta | D5 |
| 13 | Jobs de CI: SQL Server, pytest y Playwright | D10 |
| 14 | Worker Python residente, o decisión documentada de no usarlo | D11 |
| 15 | Columna `VECTOR` nativa si se migra a SQL Server 2025 | D2 (opción A) |

### Reglas para todos los PR
- Un PR por debilidad; nada de mezclar refactor y cambio de comportamiento.
- Mensajes de commit con el formato actual (`fix(rag): …`, `refactor(web): …`).
- Tras cada PR: `graphify update .` para mantener el grafo al día (sin coste de API).
- Cualquier cambio de esquema va con su migración EF y se prueba con `/health/ready`.

---

## 4. Cómo usar graphify en este proyecto

El grafo está en `graphify-out/` (ignorado por git). Úsalo para orientarte **antes** de abrir archivos grandes:

```bash
graphify update .                         # reconstruye el grafo de código, sin LLM ni coste
graphify explain "Services/Rag/RagQueryService.cs::RagQueryService"   # vecinos de un nodo
```

El grafo actual cubre solo código. Los `.md` no están incluidos porque su extracción usa un LLM y tiene coste; se añaden con `/graphify --update` desde el asistente.

Si un nombre es ambiguo, graphify lista los candidatos: repite el comando con `ruta::Símbolo`. `graphify path` sirve poco aquí, porque los caminos más cortos suelen pasar por tipos genéricos (`IOptions`, `Task`).

`graphify-out/GRAPH_REPORT.md` resume los nodos centrales (`AppDbContext`, `DocumentItem`, `OpenCodeClient`, `DocumentService`) y las comunidades. Es el punto de partida para cualquier análisis nuevo.
