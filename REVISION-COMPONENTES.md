# Revisión completa de DocuSearch AI

**Fecha:** 19/11/2026  
**Estado:** app operativa; Ollama eliminado del código, configuración, dependencias y tests.  
**Endpoint verificado:** `/api/rag/query` → `LangGraph · OpenCode · deepseek/deepseek-flash`.

## 1. Componentes de interfaz

| Componente | Archivo | Estado |
|---|---|---|
| Shell, navegación, tema y sesión | `Components/Pages/Home.razor`, `Components/Layout/` | Operativo |
| Dashboard y catálogo de agentes | `Components/Modules/Dashboard.razor` | Operativo |
| Asistente IA y panel de agentes | `Components/Modules/AgentChat.razor` | Operativo |
| Render de respuestas y fuentes | `Components/Modules/ChatAnswer.razor` | Operativo |
| Buscador híbrido | `Components/Modules/Search.razor` | Operativo |
| Gestión y visor de expedientes | `DocumentManagement.razor`, `DocumentViewer.razor` | Operativo |
| Carga y metadatos | `UploadDialog.razor`, `MetadataDialog.razor` | Operativo |
| Calidad documental e índice RAG | `Quality.razor` | Operativo |
| Auditoría | `Audit.razor` | Operativo |
| Observabilidad | `Observability.razor` | Operativo |
| Administración/RBAC y aprobaciones | `Administration.razor`, `ApprovalsPanel.razor` | Operativo |
| Uso de IA y rutinas | `AiUsagePanel.razor`, `RoutinesPanel.razor` | Operativo |
| Arquitectura e informes | `Architecture.razor`, `Reports.razor` | Operativo |
| Indicador visual de agentes | `AgentChat.razor` + `wwwroot/app.css` | Luces por agente, modal y animación |

## 2. Seguridad, identidad y estado

- `AuthService`, TOTP, cookies, bloqueo de intentos.
- `Permissions`, RBAC por rol/departamento/confidencialidad.
- `LocalSession`, `WorkspaceState`, `AssistantMemory`.
- `ApprovalService`, `GovernanceApi`, `GovernanceAudit`.
- `AgentVerifier` (AG-02) como puerta final de citas y permisos.

## 3. Documentos e ingesta

- `PdfProcessor`, `DocumentService`, `DocumentInspector`.
- `EmbeddingIndexer`, `PineconeSyncService`, `RagSourceStore`.
- Carga PDF, SQL y REST.
- Migraciones EF Core, `DatabaseSetup` con reintentos de SQL.
- `ReportExport` CSV/HTML/PDF del asistente.

## 4. RAG

| Pieza | Implementación | Estado |
|---|---|---|
| Loaders | PDF (`PdfRagLoader`), SQL (`SqlRagLoader`), REST (`RestRagLoader`) | Operativo |
| Chunking | fijo y semántico configurable | Probado |
| Embeddings | **Pinecone Inference `multilingual-e5-large` (1024)** | Reindexado |
| Vector stores | SQL Server + Pinecone | Operativo |
| Retrieval | híbrido BM25 + coseno | Probado |
| Reranker | cross-encoder FastEmbed/ONNX en host | Operativo |
| Generación | OpenCode 2/DeepSeek | Operativo |
| Endpoint | `POST /api/rag/query` autenticado | Verificado |
| Auditoría | `RAG_QUERY`, trazas y métricas | Operativo |
| Documento de salida | PDF en memoria (`RagPdfExporter`) | Probado |
| Sets de evaluación | 30 preguntas actuales, fixture seed y legacy | Operativo |

**Corpus actual:** 12 documentos, **116 fragmentos**; **92 vectores de página**.

## 5. Agentes

- AG-01 Catalogador (`CatalogSuggester`) — propuesta sin escritura.
- AG-02 Verificador (`AgentVerifier`) — determinista.
- AG-03 Recuperador (`HybridRagRetriever`).
- AG-04 Redactor (`LocalAgent` + `RagQueryService`).
- AG-05 Auditor (`AuditInspector`).
- AG-06 Observador (`AgentObservability`, `RagObservatory`).
- AG-10 Inspector (`DocumentInspector`, `RagIndexInspector`).
- `AgentCatalog` como registro central.
- `AgentResilience` y `AgentObservability`.

## 6. LangChain y LangGraph

- `LangChain/opencode_worker.py`: generación OpenCode con contrato de citas.
- `LangChain/langgraph_worker.py`: `StateGraph` `prepare → plan → synthesize → critique → finalize`.
- `LangGraphRagService`: invoca el worker, recibe fuentes ya autorizadas y aplica **AG-02** en C#.
- `LangChainRunner`: proceso Python aislado, timeout y límite de salida.
- El grafo usa **OpenCode/DeepSeek**, no Ollama.
- Fallback al RAG anterior ante timeout, JSON inválido o verificación fallida.

## 7. Eliminación de Ollama

Eliminado:

- `Services/OllamaClient.cs`.
- `LangChain/worker.py` y `LangChain/test_worker.py`.
- Dependencia `langchain-ollama`.
- Registro DI, configuración, perfiles, ramas de generación y ramas de tests.
- Variables de entorno Docker y referencias de UI/comentarios activos.

**Verificación:** `grep` de `Ollama|ollama|bge-m3|11434` sobre código/configuración/tests = **0 referencias**.

## 8. Pruebas ejecutadas

| Suite | Resultado |
|---|---|
| Base .NET | **75/75** |
| RAG .NET | **119/119** |
| Verificador, inspector, catalogador y auditor | **107/107** |
| Python LangGraph | **6/6** |
| Endpoint real | `LangGraph · OpenCode · deepseek/deepseek-flash` |
| Health/login | `ready` / `200` |

## 9. Faltantes y observaciones honestas

1. **LangGraph usa plan y crítica deterministas** para evitar tres llamadas al modelo; el prompt original pedía plan/crítica con LLM. Es una decisión de latencia, no un fallo.
2. **No hay test C# automatizado con runner LangGraph falso**; hay build, prueba manual del endpoint y 6 pruebas Python.
3. **Los embeddings ahora se envían a Pinecone Inference** (texto de fragmentos/páginas). Antes se calculaban localmente; es un cambio de privacidad/coste que debe aprobarse para datos reales.
4. **Rendimiento**: LangGraph tarda decenas de segundos; el timeout actual es 240 s y usa 4 fuentes.
5. **Documentación histórica** en `agente-ia/` y algunos documentos raíz puede mencionar Ollama; no es código ejecutable.
6. **RAGAS** no está disponible en Python 3.14; DeepEval se usa con OpenCode/DeepSeek.
7. La UI de luces está compilada y desplegada, pero no se automatizó una prueba E2E de navegador.
