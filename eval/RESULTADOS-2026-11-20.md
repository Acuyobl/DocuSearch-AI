# Evaluación RAG — 20/11/2026

## Set ejecutado

`eval/rag_questions.json` — 30 preguntas sobre el corpus actual.

## Resultado

| Métrica | Valor | Umbral |
|---|---:|---:|
| Context Recall | **0.433** | 0.80 |
| Context Precision | 0.067 | 0.50 |
| MRR | 0.306 | — |
| Faithfulness (proxy) | 0.837 | 0.90 |
| Answer Relevancy (proxy) | 0.506 | 0.80 |
| Casos sin la fuente esperada | **17 / 30** | — |

Comando:

```powershell
docker exec -e Rag__Rerank=false ds-app bash -lc \
  "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval"
```

## Interpretación

- El pipeline **responde**, pero el set actual no está alineado con el corpus/retrieval:
  fallan 17 de 30 casos.
- `Rag__Rerank=false` desactiva el cross-encoder; el resultado no representa la
  configuración con reranker real.
- La precisión está acotada porque cada caso espera una página y se recuperan varias fuentes.
- No se debe presentar este resultado como aprobación de calidad; es una línea base para
  localizar regresiones.

## Siguiente trabajo de calidad

1. Revisar las 17 preguntas fallidas y comprobar que documento/página esperados existen.
2. Ejecutar con el reranker real (`Rag__Rerank=true` y servicio en `:4098`).
3. Comparar `rag_questions.json`, `rag_questions_current.json` y `rag_questions_seed.json`
   para separar fallo de retrieval de desalineación del set.
4. Ejecutar DeepEval con juez OpenCode sobre datos de prueba y guardar el JSON de resultados.
5. Añadir un umbral de regresión en CI cuando exista una base estable; hoy CI no tiene
   servicios Pinecone/OpenCode para esta evaluación.
