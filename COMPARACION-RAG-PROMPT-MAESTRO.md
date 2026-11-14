# Comparación — Prompt Maestro RAG vs. DocuSearch AI

**Fecha:** 14/11/2026
**Referencia:** `C:\Users\Usuario\Documents\Programación\RAG\Prompt_Maestro_RAG.md`
**Implementación:** repositorio actual, con RAG híbrido local + Pinecone opcional.

---

## Resumen ejecutivo

DocuSearch AI ya tiene un **RAG funcional de extremo a extremo** sobre documentos PDF:
ingesta, extracción por página, embeddings `bge-m3`, búsqueda híbrida, citas verificadas y
generación con OpenCode 2/LangChain. También sincroniza y consulta Pinecone.

Sin embargo, **no es la implementación de 7 pasos con interfaces y evaluación que exige el
prompt maestro**. Faltan, sobre todo:

1. **Chunking real** (hoy es por página, sin tokens/overlap ni chunking semántico).
2. **Reranking** de candidatos.
3. **Evaluación RAGAS/DeepEval** y el set de 30 preguntas con métricas y umbrales.
4. **Abstracción `VectorStore`/`Loader`** para migrar de índice sin tocar la app.
5. **Endpoint REST de consulta**; hoy la consulta vive en la UI Blazor.
6. **Auditoría de fragmentos/respuesta/latencia**; hoy hay una decisión explícita de
   privacidad que omite contenido.

Lo que sí está por encima del prompt: **RBAC antes del retrieval**, verificación de citas en
C#, fallback extractivo, embeddings locales y metadatos de confidencialidad.

---

## 1. Los 7 pasos del prompt

| Paso | Estado actual | Evidencia | Brecha |
|---|---|---|---|
| **1. Ingesta** | 🟡 Parcial | `PdfProcessor` + `UploadDialog` (PDF); `LegacyImport` (JSON de respaldo); `DocumentService` | No hay `Loader` común ni 3 fuentes reales (PDF, SQL, API REST, repos, OKF). |
| **2. Chunking** | ❌ No | `PdfProcessor` crea una página = un fragmento; `EmbeddingIndexer` vectoriza la página completa | No hay 512 tokens, overlap 10-15% ni chunking semántico configurable. |
| **3. Embedding** | 🟡 Parcial | `EmbeddingClient` con `bge-m3` 1024 dims; `EmbeddingIndexer` con hash por página | Sin lotes, sin caché global por hash de fragmento; una llamada por página. |
| **4. Indexación** | 🟡 Parcial | `PageEmbeddings` en SQL Server; `PineconeClient`/`PineconeSyncService` | No hay interfaz `VectorStore` ni adaptadores intercambiables; Pinecone y SQL están acoplados a los servicios. |
| **5. Retrieval** | 🟡 Parcial | `LocalSearch` léxico + coseno + compuerta; `PineconeRetriever` | No hay BM25 real ni cross-encoder/reranking; el léxico es coincidencia de términos. |
| **6. Augmentation** | ✅ Cumple | `LocalAgent.AnswerFromSources` arma contexto con código y página; hasta 5–6 fragmentos | El contexto es por página, no por chunk; puede ser muy grande o poco preciso. |
| **7. Generation** | ✅ Cumple en lo esencial | `OpenCodeClient` + LangChain/OpenCode 2; AG-02 valida citas; fallback extractivo | La evaluación de fidelidad es por citas/texto, no con RAGAS/DeepEval. |

---

## 2. Requisitos funcionales

| Requisito | Estado | Comentario |
|---|---|---|
| Ingerir desde **3 tipos de fuente** | ❌ | Solo PDF y un JSON de respaldo; no hay API REST, repositorios ni OKF con una interfaz común. |
| Chunking fijo y semántico configurable | ❌ | Fragmento = página. |
| Embeddings con metadatos (fuente, fecha, posición) | 🟡 | Hay documento, código, página, modelo, departamento y confidencialidad; falta fecha de fuente y posición dentro del chunk. |
| Endpoint de consulta NL → respuesta con fuentes | ❌ | La consulta está en la UI Blazor (`/search`, asistente); no hay endpoint REST de consulta. |
| Búsqueda híbrida + reranking | 🟡 | Híbrida sí; reranking no. |
| Registrar query, fragmentos, respuesta y latencia | 🟡/❌ | `SEARCH` guarda la consulta en auditoría; `AGENT_QUERY` omite contenido por privacidad; las trazas guardan latencia/modelo/tokens, no fragmentos ni respuesta. |
| Migrar de vector DB sin reescribir la app | ❌ | No hay `VectorStore`; SQL y Pinecone se usan desde servicios concretos. |

---

## 3. Evaluación y criterios de aceptación

| Criterio del prompt | Estado | Evidencia |
|---|---|---|
| Pipeline de 7 pasos end-to-end | 🟡 | Funciona el flujo PDF → página → embedding → híbrido → generación → citas, pero no con chunking ni interfaces del prompt. |
| Context Recall ≥ 0.8 | ❌ | No medido. |
| Faithfulness ≥ 0.9 | ❌ | No medido con RAGAS/DeepEval; AG-02 valida citas. |
| `VectorStore` abstraído con 2 implementaciones | 🟡 | Existen SQL Server y Pinecone, pero **sin interfaz común**. |
| Híbrida + reranking vs. baseline | 🟡 | Híbrida sí; reranking y baseline medido, no. |
| Toda respuesta cita fuente (documento + fragmento) | ✅ | Código + página; citas validadas por AG-02. |
| Set de 30 preguntas con respuesta/fuente | ❌ | Hay preguntas manuales, no un set formal versionado con métricas. |
| RAGAS/DeepEval integrado | ❌ | No hay dependencias ni harness de evaluación. |

---

## 4. Diferencias deliberadas (no son olvidos)

1. **Privacidad vs. auditoría:** el prompt pide registrar query, fragmentos, respuesta y
   latencia. La app evita guardar contenido en trazas y en `AGENT_QUERY`; `SEARCH` sí guarda
   la consulta. Es una decisión de privacidad, pero **incumple el requisito de auditoría
   completa**. Hay que decidir política: contenido, hashes, longitudes o solo metadatos.
2. **Pinecone vs. Chroma:** el prompt sugiere Chroma para prototipo; la app usa SQL Server
   local y Pinecone opcional. No hay que añadir Chroma si no se necesita; sí conviene la
   interfaz `VectorStore`.
3. **bge-m3 local vs. text-embedding-3-large:** se eligió local por privacidad; es una
   alternativa open-source válida, pero no cumple literalmente el ejemplo del prompt.

---

## 5. Lo que ya está mejor que el prompt

- **RBAC antes del retrieval:** los documentos se filtran por permisos y confidencialidad;
  Pinecone recibe solo IDs autorizados.
- **Verificación AG-02:** valida citas, páginas, fuente y texto; el prompt solo pide citar.
- **Fallback extractivo:** si no hay motor o no hay contexto, no inventa.
- **Pinecone opcional:** sincroniza vectores y metadatos, sin texto; SQL Server sigue como
  respaldo.
- **Ingesta observable:** progreso por pasos, auditoría y estado OCR.

---

## 6. Brechas prioritarias y plan recomendado

### Prioridad 0 — Medir antes de optimizar

- Crear un set versionado de **30+ preguntas** con documento, páginas esperadas y respuesta.
- Implementar un harness de evaluación (RAGAS/DeepEval o métricas propias) para:
  - Context Recall
  - Context Precision
  - Faithfulness
  - Answer Relevancy
- Establecer baseline y no cambiar chunking/retrieval sin números.

### Prioridad 1 — Abstracciones que el prompt exige

- `Loader` común: PDF, SQL/JSON y API REST.
- `VectorStore` con adaptadores `SqlServerVectorStore` y `PineconeVectorStore`.
- `Chunker` con estrategia fija (512 tokens, overlap 10-15%) y semántica.
- Endpoint autenticado de consulta si se necesita integrar fuera de Blazor.

### Prioridad 2 — Calidad de retrieval

- Chunking por tokens con solape, conservando página y offsets para citas.
- BM25 o híbrido léxico real; luego cross-encoder/reranking.
- Medir Context Recall y Precision contra el baseline.

### Prioridad 3 — Operación

- Política de auditoría (contenido vs. metadatos) aprobada.
- Batch de embeddings y caché por hash de fragmento.
- Evaluación continua en CI o job programado.

---

## 7. Conclusión

El proyecto tiene **RAG funcional y con controles de seguridad sólidos**, pero **no cumple
todavía el prompt maestro como especificación de producción**. La diferencia principal no es
Pinecone: es la falta de **chunking real, reranking, evaluación objetiva y abstracciones de
VectorStore/Loader**. Lo recomendable es **medir primero con un set de 30 preguntas** y luego
invertir en chunking/reranking según los resultados, no reescribir todo de golpe.
