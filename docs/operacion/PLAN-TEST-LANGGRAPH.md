# Plan de pruebas C# — LangGraph con runner falso

**Objetivo:** probar la integración C# de LangGraph sin Python, sin OpenCode, sin Pinecone y sin red, verificando contrato, citas, fallback, permisos y bandera.

**Estado actual:** `LangGraphRagService` llama directamente a `LangChainRunner.RunAsync` (estático). Eso impide inyectar un runner falso. Primero hay que introducir un seam mínimo; después se añaden las pruebas.

---

## Fase 0 — Seam de testabilidad

### 0.1 Runner inyectable

Crear en `Services/Rag/LangGraphRagService.cs` (o archivo propio):

```csharp
public interface ILangGraphRunner
{
    Task<string> RunAsync(string pythonExecutable, string scriptFile, int timeoutSeconds,
        object? request, CancellationToken cancellationToken = default);
}

public sealed class ProcessLangGraphRunner : ILangGraphRunner
{
    public Task<string> RunAsync(string pythonExecutable, string scriptFile, int timeoutSeconds,
        object? request, CancellationToken cancellationToken = default) =>
        LangChainRunner.RunAsync(pythonExecutable, "langgraph_worker.py", timeoutSeconds, request);
}
```

`LangGraphRagService` debe recibir `ILangGraphRunner` en el constructor. Registrar `ProcessLangGraphRunner` en `Program.cs`.

### 0.2 Runtime del asistente inyectable

`LangGraphRagService` no debe depender de la clase concreta `AssistantRuntime` para probar el catálogo. Introducir:

```csharp
public interface IAssistantModelRuntime
{
    AssistantProfile Active { get; }
    Task EnsureLoadedAsync();
}
```

`AssistantRuntime` lo implementa; `LangGraphRagService` recibe `IAssistantModelRuntime`. En tests se usa un fake con un `AssistantProfile` de OpenCode/DeepSeek.

### 0.3 Sin cambios de comportamiento

- `ProcessLangGraphRunner` conserva exactamente `LangChainRunner`.
- `AssistantRuntime` conserva su comportamiento actual.
- La bandera `Assistant:UseLangGraph` sigue apagada por defecto.

---

## Fase 1 — Fixtures deterministas

En `tests/Program.cs`, dentro de `if (args.Contains("--langgraph"))`, crear:

```csharp
sealed class FakeLangGraphRunner : ILangGraphRunner
{
    public Queue<string> Responses { get; } = new();
    public Exception? Failure { get; init; }
    public string? LastRequest { get; private set; }
    public int Calls { get; private set; }

    public Task<string> RunAsync(...)
    {
        Calls++;
        LastRequest = JsonSerializer.Serialize(request);
        if (Failure is not null) return Task.FromException<string>(Failure);
        return Task.FromResult(Responses.Dequeue());
    }
}
```

Datos:

- `AssistantProfile` fake: `Engine="opencode"`, `ProviderId="deepseek"`, `ModelId="deepseek-flash"`, `Available=true`.
- `AssistantOptions { UseLangGraph = true, LangGraphMaxSources = 6, LangGraphTimeoutSeconds = 30 }`.
- `OpenCodeOptions { Enabled=true, BaseUrl="http://127.0.0.1:4097", Password="secreto", PythonExecutable="/usr/bin/python3" }`.
- `AppSnapshot` con un usuario administrador y dos documentos con páginas cuyo texto contenga el `Snippet` de las fuentes.
- `SearchHit` con página 1 y 2, snippets inequívocos.

Respuestas fake:

```json
{"status":"ok","answer":"Resumen [1].","citations":[1],"steps":["prepare","plan","synthesize","critique","finalize"],"usage":{"model_calls":3}}
```

---

## Fase 2 — Pruebas unitarias de `LangGraphRagService`

| # | Caso | Resultado esperado |
|---|---|---|
| LG-01 | Bandera `UseLangGraph=false` | Devuelve `null`; runner no se llama |
| LG-02 | OpenCode desactivado | Devuelve `null`; runner no se llama |
| LG-03 | Respuesta válida con `citations:[1,2]` | Devuelve `AgentAnswer`; 2 fuentes; AG-02 válido |
| LG-04 | `citations:[2]` | Devuelve solo la fuente 2 |
| LG-05 | `citations:[]` | Devuelve `null` |
| LG-06 | `citations:[9]` | Devuelve `null` |
| LG-07 | JSON inválido | Devuelve `null`; no lanza |
| LG-08 | Runner lanza timeout | Devuelve `null`; no lanza |
| LG-09 | Runner lanza error de proceso | Devuelve `null`; no lanza |
| LG-10 | AG-02 rechaza texto/página | Devuelve `null` |
| LG-11 | Fuente con página inexistente | Devuelve `null` |
| LG-12 | `sources` vacías | Devuelve `null`; runner no se llama |
| LG-13 | 10 fuentes recibidas | El request contiene **6**; se respeta el límite |
| LG-14 | Texto de fuente largo | El request lo trunca a **800** caracteres |
| LG-15 | Petición generada | Contiene `provider_id`, `model_id`, `password`, `question`, `sources`, `system` |
| LG-16 | Respuesta válida | `AgentAnswer.Provider` contiene `LangGraph` y `deepseek` |
| LG-17 | `UseLangGraph=true` y runner vacío | Devuelve `null`, sin excepción |

---

## Fase 3 — Pruebas de integración con `RagQueryService`

Para no construir todo el grafo RAG con mocks frágiles, extraer o inyectar un pequeño contrato:

```csharp
public interface IRagGraphOrchestrator
{
    Task<AgentAnswer?> TryGenerateAsync(...);
}
```

`LangGraphRagService` lo implementa. `RagQueryService` recibe `IRagGraphOrchestrator?` y solo lo usa en `AnswerStyle.Summary/Analysis`.

| # | Caso | Resultado esperado |
|---|---|---|
| RG-01 | Pregunta normal | No llama al orquestador; usa el camino anterior |
| RG-02 | Resumen con orquestador válido | Devuelve la respuesta del grafo; `engine`/auditoría `rag-langgraph` |
| RG-03 | Resumen con orquestador `null` | Usa el camino anterior |
| RG-04 | Resumen con orquestador que lanza | Usa el camino anterior sin mostrar error |
| RG-05 | Citas del grafo fuera de rango | Orquestador devuelve `null`; camino anterior |
| RG-06 | Fuentes autorizadas | El orquestador recibe solo fuentes del snapshot permitido |
| RG-07 | `AuditContent=false` | La auditoría no guarda pregunta/respuesta en claro |

---

## Fase 4 — Contrato y seguridad

- **No secretos en logs:** el request contiene password, pero ningún `ILogger` lo imprime.
- **No red:** el fake no abre sockets; la prueba falla si se llama a Python/OpenCode real.
- **No escritura:** el servicio no modifica documentos, aprobaciones ni tablas.
- **AG-02:** una respuesta con cita inventada nunca se devuelve.
- **RBAC:** fuentes no autorizadas no se añaden al request (la responsabilidad principal está en `RagQueryService`; el test verifica el snapshot).
- **Contrato JSON:** todos los campos obligatorios existen y tienen tipo correcto.
- **Límites:** timeout, número de fuentes, longitud de texto y tamaño de respuesta.

---

## Fase 5 — CI y comandos

Añadir al final de `tests/Program.cs` el bloque `--langgraph` y documentarlo:

```powershell
dotnet run --project tests/DocuSearch.Checks.csproj -c Debug -- --langgraph
```

Comando combinado:

```powershell
docker run --rm -v "${PWD}:/src:ro" ds-app-langchain:latest bash -c 'mkdir /t && cp -a /src/. /t/ && rm -rf /t/bin /t/obj /t/tests/bin /t/tests/obj && cd /t && dotnet run --project tests/DocuSearch.Checks.csproj -c Debug -- --langgraph --rag'
```

Actualizar `tests/run-all.ps1` y CI para incluir `--langgraph`.

---

## Criterios de aceptación

- [ ] `LangGraphRagService` no llama estáticamente a Python; recibe `ILangGraphRunner`.
- [ ] El runtime del asistente es inyectable mediante una interfaz.
- [ ] Existen al menos **17 pruebas LG** y **7 pruebas RG**.
- [ ] Ninguna prueba abre red, Python, OpenCode ni Pinecone.
- [ ] Una respuesta inválida o un error del runner cae al camino anterior sin romper la app.
- [ ] AG-02 se ejecuta siempre antes de devolver una respuesta del grafo.
- [ ] La bandera apagada no invoca el runner.
- [ ] Los tests actuales (`--rag`, base, extras) siguen pasando.
- [ ] `--langgraph` se ejecuta en CI y en `run-all.ps1`.
- [ ] El total de pruebas aumenta y el resultado final es **0 fallos**.

---

## Orden de implementación

1. Crear `ILangGraphRunner` + `ProcessLangGraphRunner` y registrar DI.
2. Crear `IAssistantModelRuntime` e implementarlo en `AssistantRuntime`.
3. Adaptar `LangGraphRagService` sin cambiar comportamiento.
4. Añadir `FakeLangGraphRunner` y fixtures.
5. Añadir pruebas LG-01 a LG-17.
6. Añadir `IRagGraphOrchestrator` y pruebas RG.
7. Ejecutar `--langgraph --rag`, corregir y documentar.
8. Commit único: `test: cobertura C# de LangGraph con runner falso`.
