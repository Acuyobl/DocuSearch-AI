# LangGraph en DocuSearch AI — implementación

**Estado:** MVP funcional. Usa **OpenCode 2 + DeepSeek**; no usa Ollama como modelo de agente.

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
| Proveedor | OpenCode 2 | `deepseek/deepseek-flash` |
| Dependencia | `LangChain/requirements.txt` | `langgraph==1.2.23` |

Los embeddings usan **Pinecone Inference** (`multilingual-e5-large`, 1024 dimensiones), no Ollama.

## Flujo

```text
RagQueryService (C#): recupera y autoriza fuentes (AG-03)
  → LangGraphRagService (C#): serializa hasta 4 fuentes, 800 caracteres
  → langgraph_worker.py:
       prepare → plan (determinista) → synthesize (1 llamada OpenCode/DeepSeek)
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

`appsettings.json` deja la bandera apagada por defecto:

```json
"Assistant": {
  "UseLangGraph": false,
  "LangGraphTimeoutSeconds": 240,
  "LangGraphMaxSources": 4,
  "LangGraphMaxRetries": 1
}
```

Para activarlo en el entorno Docker:

```text
Assistant__UseLangGraph=true
OpenCode__Enabled=true
OpenCode__BaseUrl=http://host.docker.internal:4097
OpenCode__Password=...
OpenCode__PythonExecutable=/usr/bin/python3
```

El worker se comprueba con:

```powershell
docker exec ds-app python3 /work/LangChain/langgraph_worker.py --check
```

## Evidencia

- `--check`: `{"status":"ready","framework":"LangGraph","provider":"opencode-2"}`
- Pruebas Python: **6/6**.
- Prueba C# completa con `/api/rag/query`:
  - `engine = LangGraph · OpenCode · deepseek/deepseek-flash`
  - respuesta con citas y fuentes.
- Fallback comprobado: si el worker tarda o falla, `RagQueryService` continúa con el camino anterior.

## Decisiones para el MVP

- **OpenCode 2/DeepSeek** como proveedor; el worker no habla con Ollama.
- **Plan y crítica deterministas** para gastar una sola llamada generativa.
- **4 fuentes y 800 caracteres por fuente** para acotar latencia y contexto.
- **LangGraph solo para resumen/análisis**; preguntas normales, catalogador, auditor, inspector y observador siguen en C#.
- **La bandera está apagada por defecto**; no se cambia el comportamiento si no se activa.

## Faltantes / próximos pasos

1. **Embeddings**: `Embeddings` sigue usando `bge-m3` en Ollama. DeepSeek/OpenCode no ofrecen un modelo de embeddings equivalente. Quitarlo exige elegir otro proveedor (por ejemplo, inferencia de Pinecone) y **reindexar los 109 fragmentos y las páginas**.
2. **Plan/crítica con LLM** opcional para recuperar la topología completa del prompt original.
3. **Prueba C# automatizada** con un runner falso; hoy la validación es build + prueba manual del endpoint.
4. **Instalación reproducible**: actualizar la imagen/`app-start.sh` para incluir `langgraph` (el script local ya lo contempla, pero `obj/` no se versiona).
5. **Streaming/checkpoints** no implementados.
6. **Citas**: se deduplican por documento+página; el `chunkIndex` del grafo queda en 0 porque el worker no recibe el índice del fragmento.
