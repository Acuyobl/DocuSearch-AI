# Auditoría del RAG de DocuSearch

**Fecha:** 27/11/2026  
**Estado:** operativo; respuestas generadas con OpenCode/DeepSeek y fallback extractivo corregido.

## Flujo actual

```text
Pregunta
  → memoria conversacional (solo seguimientos reales)
  → corrección de erratas por vocabulario
  → embedding Pinecone multilingual-e5-large
  → retrieval híbrido: vector + BM25
  → reranker semántico (cross-encoder no disponible)
  → selección de hasta 8 fuentes
  → prompt estructurado
  → OpenCode/DeepSeek (LangChain/LangGraph)
  → AG-02 verifica citas
  → respuesta + fuentes
```

## Correcciones aplicadas

1. **Falso seguimiento conversacional**
   - Antes: cualquier pregunta de ≤3 palabras heredaba la conversación anterior.
   - “Fabic” heredaba Mesopotamia.
   - Ahora solo es seguimiento si empieza con conector (“y”, “pero”…) o contiene una anáfora (“ese documento”, “el anterior”…).

2. **Corrección de erratas**
   - Se agregó `QueryCorrector`.
   - Usa títulos y palabras clave de los documentos legibles.
   - Ejemplo: **Fabic → Fabric**.

3. **Sin fuentes irrelevantes**
   - `HybridRagRetriever` descarta candidatos con score muy inferior al mejor.
   - `RagQueryService` se abstiene si no hay cobertura léxica y la similitud es baja.

4. **Fallback extractivo útil**
   - Antes concatenaba todos los fragments, incluidos los irrelevantes.
   - Ahora usa solo las 3 fuentes más relevantes y no mezcla metadatos de demo.

5. **Más contexto**
   - Fragmentos enviados al modelo: hasta 1500 caracteres.
   - Prompt breve reestructurado: respuesta directa + puntos clave + límite de fuentes.

6. **Respuestas verificadas**
   - `Fabic` → responde sobre Microsoft Fabric.
   - `¿Qué es Microsoft Fabric?` → respuesta generada con fuentes correctas.
   - `SLA del contrato` → respuesta generada y verificada.

## Problemas pendientes

### 1. Recall del set de evaluación

Última evaluación nativa:

```text
30 casos
Context Recall:     0.433
Context Precision:  0.067
MRR:                0.305
Faithfulness:       0.946
Answer Relevancy:   0.497
Casos sin fuente:   17/30
```

El `faithfulness` es alto, pero el retrieval no está encontrando la fuente esperada en 17 casos. No es solo un problema de redacción: hay que mejorar recuperación/set de evaluación.

### 2. Cross-encoder no disponible

`Rag:RerankerUrl` apunta a `http://host.docker.internal:4098`, pero el microservicio no está levantado. El sistema usa el reranker semántico como fallback. No bloquea, pero no es el reranker ideal.

### 3. Modelo

El perfil activo es `deepseek-flash`, rápido y económico. Para respuestas más analíticas está disponible `deepseek-v4-pro`; cambiarlo aumenta calidad y coste/latencia.

### 4. Fragmentos faltantes

Hay documentos/páginas sin cobertura completa en `RagFragments`/embeddings. La evaluación lo refleja. Conviene reindexar y validar cobertura por documento.

## Siguientes pasos recomendados

1. Reindexar con modelo actual y verificar embeddings por página.
2. Revisar los 17 casos fallidos y separar:
   - documento no indexado,
   - documento indexado pero mal ranking,
   - pregunta ambigua.
3. Levantar el cross-encoder o medir explícitamente el reranker semántico.
4. Probar `deepseek-v4-pro` con el mismo set y comparar.
5. Ajustar `CandidateK`, `TopK` y chunking con datos, no por intuición.
6. Añadir un conjunto de preguntas de regresión por documento.

## Pruebas

```text
Base:       75/75
RAG:       119/119
LangGraph:  80/80
E2E Web:     1/1
```

La app está levantada en `http://127.0.0.1:5068/web`.
