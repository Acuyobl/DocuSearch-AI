# Revisión de agentes y prompt para LangGraph

**Estado revisado:** DocuSearch AI .NET 10, RAG por fragmentos, Pinecone/SQL, asistente con OpenCode/Ollama y verificación determinista.

## 1. Revisión real de los agentes

| Agente | Estado actual | Archivos principales | ¿LangGraph aporta? |
|---|---|---|---|
| **AG-04 Redactor** | Asistente activo: recupera fuentes, genera con OpenCode/Ollama y cae a extractivo | `Services/LocalSearch.cs`, `Services/Rag/RagQueryService.cs`, `Services/AssistantRuntime.cs`, `Services/OpenCodeClient.cs`, `Services/OllamaClient.cs` | Sí, solo para un flujo acotado de síntesis; no para reemplazar el asistente |
| **AG-03 Recuperador** | Híbrido BM25 + vectorial, Pinecone/SQL y reranker | `Services/Rag/HybridRagRetriever.cs`, `Services/Rag/PineconeRagVectorStore.cs` | No. Debe seguir en C#: permisos, fuentes y latencia |
| **AG-02 Verificador** | Determinista; valida citas, páginas, permisos, duplicados y texto | `Services/AgentVerifier.cs` | No. Debe seguir siendo la puerta final en C# |
| **AG-01 Catalogador** | Una llamada opt-in, propuesta sin escritura y verificación AG-02 | `Services/CatalogSuggester.cs` | No en la primera fase; su valor es el contrato y la aprobación humana |
| **AG-05 Auditor** | Informe determinista de la bitácora | `Services/AuditInspector.cs` | No |
| **AG-06 Observador** | Métricas y trazas del asistente | `Services/AgentObservability.cs`, `Services/Rag/RagObservatory.cs` | No como agente; sí como consumidor de trazas del grafo |
| **AG-10 Inspector** | Calidad documental e índice RAG | `Services/DocumentInspector.cs`, `Services/RagIndexInspector.cs` | No |

### Conclusión de la revisión

La arquitectura actual no es “un montón de agentes sin orquestar”. Es deliberadamente **C# como autoridad**: el modelo propone, C# recupera, autoriza, verifica y audita. `MULTIAGENTES-ANALISIS.md` ya advierte que LangGraph/CrewAI/AutoGen serían sobreingeniería para el alcance actual.

Por tanto, la adopción correcta de LangGraph es **limitada y reversible**:

- Un grafo Python **de solo lectura** para síntesis/análisis multi-paso.
- La recuperación, el RBAC, la persistencia y la verificación siguen en C#.
- El grafo **no recibe herramientas**, no consulta SQL, no escribe documentos y no decide permisos.
- Se activa con una bandera; si falla, se usa el camino actual.
- No se reescriben AG-02, AG-05, AG-06 ni AG-10.

## 2. Arquitectura propuesta (MVP)

```text
Blazor / API
   │
   ▼
RagQueryService (C#)  ──► recupera fuentes autorizadas (AG-03)
   │                         │
   │                         ├─► AgentVerifier (AG-02) sigue siendo la puerta final
   │                         └─► auditoría / trazas AG-06
   │
   ├─ Assistant:UseLangGraph=false ──► camino actual (sin cambios)
   │
   └─ Assistant:UseLangGraph=true
          │
          ▼
   LangGraphRagService (C#): serializa pregunta + fuentes ya autorizadas
          │
          ▼
   LangChain/langgraph_worker.py
     StateGraph:
       prepare → plan → synthesize → critique
                    ↑                 │
                    └── retry (máx. 1) ┘
                                  │
                             finalize (JSON estructurado)
          │
          ▼
   C# valida JSON + citas con AgentVerifier y devuelve la respuesta
```

### Reglas de diseño

1. **El grafo no recupera documentos.** Recibe hasta `N` fuentes ya autorizadas y truncadas.
2. **El grafo no escribe.** Devuelve una propuesta; C# decide y audita.
3. **Sin tool calling libre.** No hay herramientas, shell, SQL, HTTP ni acceso al sistema de archivos desde LangGraph.
4. **Máximo 3 llamadas al modelo** (plan, síntesis y crítica; un reintento opcional), con timeout y presupuesto.
5. **La salida es JSON validable**; una respuesta inválida no se muestra.
6. **Fallback obligatorio:** cualquier excepción, timeout o JSON inválido vuelve al camino actual de `RagQueryService`.
7. **Sin cambios en `AgentTrace`.** Las métricas nuevas van a logs/auditoría o a una tabla futura `AgentSteps`.

---

# PROMPT PARA IMPLEMENTAR

Copia este bloque completo en el agente de implementación.

---

Actúa como ingeniero senior .NET + Python en el repositorio **DocuSearch AI** (Blazor Server, .NET 10, EF Core, SQL Server, Pinecone, Python/LangChain). Tu tarea es implementar una integración **mínima, segura y reversible de LangGraph** para el flujo de síntesis/análisis del asistente, sin romper la app existente.

## Contexto obligatorio: léelo antes de tocar código

Lee y respeta:

- `MULTIAGENTES-ANALISIS.md` (advertencia sobre no sobreingeniería).
- `Services/LocalSearch.cs`, `Services/Rag/RagQueryService.cs`.
- `Services/Rag/HybridRagRetriever.cs`, `Services/Rag/PineconeRagVectorStore.cs`.
- `Services/AgentVerifier.cs` (AG-02).
- `Services/LangChainRunner.cs`, `LangChain/worker.py`, `LangChain/opencode_worker.py`, `LangChain/requirements.txt`.
- `Services/AssistantRuntime.cs`, `Services/OpenCodeClient.cs`, `Services/OllamaClient.cs`.
- `Components/Modules/AgentChat.razor`, `Program.cs`, `appsettings.json`.
- `tests/Program.cs` y `tests/run-all.ps1`.

No cambies la arquitectura de seguridad: **el modelo propone; C# recupera, autoriza, verifica, ejecuta y audita**.

## Objetivo

Añadir un orquestador LangGraph **opcional** para responder solicitudes de resumen, análisis o comparación sobre fuentes ya recuperadas y autorizadas. Debe:

- mantener el comportamiento actual si la bandera está desactivada;
- usar LangGraph solo en Python;
- conservar AG-02 en C# como validación final;
- degradar al camino actual ante cualquier fallo;
- no implementar agentes autónomos, colas ni tool calling libre.

## Alcance permitido

1. Crear `LangChain/langgraph_worker.py`.
2. Añadir dependencias LangGraph a `LangChain/requirements.txt`, con versiones fijadas y compatibles con el Python del proyecto.
3. Crear un runner .NET (por ejemplo `Services/LangGraphRunner.cs`) que invoque el worker por `stdin/stdout` JSON, reutilizando el patrón de `LangChainRunner` (proceso aislado, timeout, límite de bytes, matar proceso al vencer).
4. Crear un servicio C# `Services/Rag/LangGraphRagService.cs` que:
   - reciba pregunta, fuentes autorizadas y una `ConversationHint` opcional;
   - valide que haya fuentes;
   - llame al worker;
   - parsee el JSON;
   - aplique `AgentVerifier.Verify` antes de devolver;
   - devuelva un resultado con `Answer`, `Sources` y metadatos de ejecución.
5. Integrar en `RagQueryService` **solo** para `AnswerStyle.Summary`/`Analysis` cuando `Assistant:UseLangGraph=true`; el resto de consultas no cambia.
6. Añadir configuración:
   ```json
   "Assistant": {
     "UseLangGraph": false,
     "LangGraphTimeoutSeconds": 90,
     "LangGraphMaxSources": 6,
     "LangGraphMaxRetries": 1
   }
   ```
7. Documentar en `README.md` o `AGENTE-DETALLE.md`: activación, límites, privacidad y fallback.
8. Añadir pruebas deterministas.

## Topología LangGraph obligatoria

Implementa un `StateGraph` con este estado (ajusta nombres si lo justificas):

```python
class GraphState(TypedDict):
    question: str
    sources: list[dict]      # id, code, title, page, text
    plan: list[str]
    draft: str
    critique: str
    final: str
    citations: list[int]
    attempts: int
    error: str | None
```

Nodos:

1. `prepare`: valida IDs contiguos, elimina duplicados, trunca fuentes a `LangGraphMaxSources`, no llama al modelo.
2. `plan`: una llamada al modelo para producir un plan breve en JSON (`{"steps": [...]}`).
3. `synthesize`: una llamada al modelo con pregunta + plan + fuentes; devuelve borrador y citas.
4. `critique`: una llamada al modelo para comprobar cobertura y citas; devuelve JSON `{"ok": bool, "issues": [...], "citations": [...]}`.
5. `finalize`: determinista; construye el JSON final `{"answer": "...", "citations": [1,2], "steps": [...]}`. No llama al modelo.
6. Arista condicional: si `critique.ok == false` y `attempts < LangGraphMaxRetries`, volver a `synthesize`; si no, `finalize`.
7. Cualquier estado inválido termina en `finalize` con `error`, nunca en respuesta inventada.

El grafo **no** debe:
- consultar SQL/Pinecone;
- abrir HTTP distinto del proveedor LLM ya configurado;
- ejecutar herramientas;
- escribir archivos;
- decidir permisos;
- enviar contenido fuera de las fuentes recibidas.

## Contrato del worker

Entrada por stdin:

```json
{
  "question": "...",
  "sources": [
    {"id": 1, "code": "MAN-2026-...", "title": "...", "page": 1, "text": "..."}
  ],
  "max_sources": 6,
  "max_retries": 1
}
```

Salida por stdout:

```json
{
  "status": "ok",
  "answer": "...",
  "citations": [1, 2],
  "steps": ["plan", "synthesize", "critique", "finalize"],
  "usage": {"model_calls": 3}
}
```

Errores: `{"status":"error","code":"...","message":"..."}` y código de salida distinto de 0. No incluyas el texto de las fuentes ni secretos en los mensajes de error.

## Integración .NET obligatoria

- `LangGraphRagService` debe ser **Scoped** y recibir `IOptions<AssistantOptions>`/`IOptions<LangGraphOptions>`, `AgentVerifier` o el servicio de snapshot, y el runner.
- No capturar servicios Scoped en Singleton.
- No crear una identidad artificial: la sesión y el usuario ya están en `LocalSession`/`RagQueryService`; el worker solo recibe fuentes ya filtradas por RBAC.
- No ampliar `AgentTrace`. Registra el resultado en la auditoría existente con una acción `RAG_GRAPH` o log estructurado sin contenido.
- Usar `CancellationToken` de la consulta; el runner debe matar el proceso al vencer.
- Limitar `stdout`/`stderr` y el tamaño de entrada.
- Si `UseLangGraph=false`, el código actual debe ejecutarse exactamente igual.
- Si LangGraph falla, `RagQueryService` debe continuar con su ruta actual; no mostrar error al usuario.

## Seguridad y privacidad

- Solo fuentes ya autorizadas para el actor.
- No incluir documentos `Confidencial`/`Estrictamente Secreto` si el proveedor es remoto y la política no lo permite; conserva la lógica existente.
- No permitir que el modelo añada citas fuera de `1..N`.
- Validar que cada página citada existe en la fuente y que el texto no inventa contenido.
- No registrar pregunta, respuesta ni fragmentos completos si `Rag:AuditContent=false`.
- No añadir secretos a `appsettings.json`; usar variables de entorno.
- Verificar compatibilidad de LangGraph con el Python del proyecto; no actualizar paquetes a ciegas.

## Pruebas mínimas

### Python

- `prepare` con IDs no contiguos, duplicados y exceso de fuentes.
- plan/síntesis/crítica con LLM falso.
- JSON inválido del LLM → error controlado.
- crítica negativa → reintento y luego finalize.
- límite de reintentos.
- no llamadas a red desde el grafo (mock).
- salida dentro del contrato.

### .NET

- `UseLangGraph=false` conserva el comportamiento actual.
- `UseLangGraph=true` con runner falso devuelve respuesta verificada.
- citas inválidas → se descarta y se usa fallback.
- timeout/JSON inválido → fallback sin excepción visible.
- RBAC: no se envían fuentes no autorizadas.
- no se rompe `AgentTrace`; no hay nuevas escrituras no auditadas.
- Se conservan las suites existentes: `--rag` y las suites de verificación; añade checks nuevos sin reducir el total.

## Criterios de aceptación

- [ ] `Assistant:UseLangGraph=false` no cambia ninguna respuesta ni ruta existente.
- [ ] Con `true`, solo las solicitudes de resumen/análisis pasan por el grafo.
- [ ] El grafo no tiene herramientas, no consulta la base y no escribe.
- [ ] Toda respuesta pasa por AG-02 en C# antes de mostrarse.
- [ ] Un fallo del modelo, timeout o JSON inválido cae al camino anterior.
- [ ] No se filtran fuentes no autorizadas ni contenido en logs.
- [ ] Se añaden pruebas Python y .NET; las suites existentes siguen pasando.
- [ ] README/AGENTE-DETALLE documenta activación, límites y riesgos.
- [ ] El cambio es reversible: una bandera y un worker nuevos; sin migración obligatoria.

## Entrega

1. Implementa en pasos pequeños y compila después de cada uno.
2. No hagas commit hasta que compilen la app y las pruebas.
3. Al terminar, reporta:
   - archivos creados/modificados;
   - comandos ejecutados y resultados;
   - número de pruebas;
   - riesgos pendientes;
   - cómo activar y desactivar LangGraph.
4. Si LangGraph no es compatible con el Python del proyecto, detente y explica la incompatibilidad; no fuerces una actualización global.

---

## 3. Decisión recomendada

No migrar los siete agentes a LangGraph. Empezar por **un único flujo de síntesis/análisis de solo lectura** con bandera, con las fuentes ya autorizadas por C#. Si el MVP no demuestra mejor calidad o menor latencia que `RagQueryService`, no ampliarlo.

LangGraph puede aportar valor en el **reintento de crítica** y en la **planificación explícita**, pero no en RBAC, recuperación, persistencia ni verificación. Esas siguen siendo responsabilidad de C#.
