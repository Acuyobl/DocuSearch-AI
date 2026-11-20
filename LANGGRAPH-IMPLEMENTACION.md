# LangGraph en DocuSearch AI — implementación

**Estado:** MVP funcional y acotado. La bandera está desactivada en `appsettings.json`; el contenedor de prueba la activa por variable de entorno.

## Qué se implementó

| Pieza | Archivo | Estado |
|---|---|---|
| Grafo LangGraph | `LangChain/langgraph_worker.py` | Funcional |
| Pruebas del grafo | `LangChain/test_langgraph_worker.py` | 6/6 |
| Runner .NET | reutiliza `Services/LangChainRunner.cs` | Funcional |
| Servicio C# | `Services/Rag/LangGraphRagService.cs` | Funcional |
| Integración RAG | `Services/Rag/RagQueryService.cs` | Solo resumen/análisis |
| Bandera/config | `Services/AssistantRuntime.cs`, `appsettings.json` | `UseLangGraph=false` |
| Verificación | `Services/AgentVerifier.cs` (AG-02) | Se aplica después del grafo |
| Dependencia | `LangChain/requirements.txt` | `langgraph==1.2.23` |

## Flujo

```text
RagQueryService (C#): recupera y autoriza fuentes (AG-03)
  → LangGraphRagService (C#): serializa hasta 4 fuentes, 800 caracteres
  → langgraph_worker.py:
       prepare → plan (determinista) → synthesize (1 llamada LLM)
       → critique (determinista) → finalize
  → C#: AgentVerifier (AG-02)
  → respuesta o fallback al camino anterior
```

El grafo:
- no consulta SQL ni Pinecone;
- no usa herramientas;
- no escribe documentos;
- no decide permisos;
- solo recibe fuentes ya autorizadas;
- devuelve JSON con `answer` y `citations`.

## Activación

Valores por defecto en `appsettings.json`:

```json
"Assistant": {
  "UseLangGraph": false,
  "LangGraphTimeoutSeconds": 240,
  "LangGraphMaxSources": 4,
  "LangGraphMaxRetries": 1
}
```

Para el entorno Docker de prueba:

```text
Assistant__UseLangGraph=true
Ollama__Enabled=true
Ollama__BaseUrl=http://host.docker.internal:11434
Ollama__Model=qwen2.5:3b
Ollama__PythonExecutable=/usr/bin/python3
```

El worker se comprueba con:

```powershell
docker exec ds-app python3 /work/LangChain/langgraph_worker.py --check
```

## Evidencia

- `--check`: `{"status":"ready","framework":"LangGraph","provider":"ollama-local"}`
- Pruebas Python: **6/6**.
- Prueba C# completa con `/api/rag/query`:
  - `engine = LangGraph · Ollama local · qwen2.5:3b`
  - respuesta con citas y fuentes.
- Fallback comprobado: si el worker tarda o falla, `RagQueryService` continúa con el camino anterior.

## Decisiones para el MVP

- **Plan y crítica deterministas** para no gastar 2 llamadas adicionales al modelo; con 7B el flujo de 3 llamadas superaba 240 s. Queda como mejora opcional usar un plan/crítica con LLM cuando haya un modelo más rápido.
- **4 fuentes y 800 caracteres por fuente** para acotar latencia y contexto.
- **LangGraph solo para resumen/análisis**; preguntas normales, catalogador, auditor, inspector y observador siguen en C#.
- **La bandera está apagada por defecto**; no se cambia el comportamiento si no se activa.

## Faltantes / próximos pasos

1. **Activar por defecto** solo cuando haya un modelo rápido (OpenCode/DeepSeek) o GPU; con Ollama 3B en CPU el resumen tarda decenas de segundos.
2. **Plan/crítica con LLM** opcional para recuperar la topología completa del prompt original.
3. **Prueba C# automatizada** con un runner falso; hoy la validación es build + prueba manual del endpoint.
4. **Instalación reproducible**: actualizar la imagen/`app-start.sh` para incluir `langgraph` (el script local ya lo contempla, pero `obj/` no se versiona).
5. **Streaming/checkpoints** no implementados; no son necesarios para este MVP.
6. **Citas**: se deduplican por documento+página; el `chunkIndex` del grafo queda en 0 porque el worker no recibe el índice del fragmento.
7. **Proveedor**: el worker usa Ollama local; no usa OpenCode ni proveedores remotos. Es una decisión de seguridad y una limitación de rendimiento.
