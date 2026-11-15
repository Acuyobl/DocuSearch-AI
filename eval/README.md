# Evaluación RAG

## Sets de preguntas

| Archivo | Casos | Uso |
|---|---:|---|
| `eval/rag_questions.json` (por defecto) | 14 | Preguntas **parafraseadas** sobre los 5 documentos del seed. Mide recuperación real. |
| `eval/rag_questions_current.json` | 30 | Set generado sobre los 6 expedientes actuales, sin códigos en la pregunta. Referencia actual del corpus. |
| `eval/rag_questions_legacy.json` | 30 | Set anterior sobre 3 documentos subidos. Cada pregunta incluye el código del documento y el inicio literal del fragmento, así que **infla el recall**. Se conserva solo para comparar con resultados antiguos. |

Formato de cada caso: `id`, `question`, `expectedDocumentId`, `expectedCode`, `expectedPage` y,
opcionalmente, `expectedChunkIndex`. El acierto se mide por **documento + página**; el índice de
fragmento solo se exige si se indica, porque cambia al cambiar `Rag:MaxTokens` o la estrategia.

Reglas para añadir preguntas:

- No nombrar el código del documento.
- No copiar 6 o más palabras seguidas del texto esperado; hay que parafrasear como lo haría un usuario.
- Cubrir varios documentos y páginas.

`--rag-eval` marca las preguntas que incumplen las dos primeras reglas. El test
`RAG eval: el set de preguntas es válido…` de `tests/Program.cs` lo comprueba en CI contra `Data/seed.json`.

## Evaluación .NET (sin LLM de juez)

```powershell
docker exec -e Rag__Rerank=false ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval"
# Otro set:
docker exec ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval --questions eval/rag_questions_legacy.json"
```

Mide:

- **Context Recall:** casos con la página esperada entre las fuentes.
- **Context Precision:** fracción de fuentes que son la página esperada.
- **MRR:** 1/posición de la primera fuente correcta.
- **Faithfulness y Answer Relevancy:** proxies léxicos.

Los casos cuyo documento no existe en la base (p. ej. tras borrar el seed) se omiten y se listan aparte.

Resultado histórico con el set legacy, antes de las correcciones del pipeline:

| Métrica | Valor |
|---|---:|
| Context Recall | 0.900 |
| Context Precision (proxy) | 0.154 |
| Faithfulness (proxy) | 0.966 |
| Answer Relevancy (proxy) | 0.881 |
| Casos sin fuente esperada | 3 / 30 |

Resultado de referencia actual con `eval/rag_questions_current.json` (30 casos, cross-encoder activo):

| Métrica | Valor |
|---|---:|
| Context Recall | **0.833** |
| Context Precision | 0.104 |
| MRR | 0.494 |
| Faithfulness (proxy) | **0.968** |
| Answer Relevancy (proxy) | 0.473 |
| Casos sin fuente esperada | 5 / 30 |

El set `rag_questions_legacy.json` da Recall 0.833, Precision 0.104, Faithfulness 0.973 y
5/30 fallos, con las 30 preguntas marcadas por revelar el código. El set por defecto
(`rag_questions.json`) es el fixture del seed que usa el test `--rag`.

Los proxies **no sustituyen DeepEval**. La precisión está acotada por diseño: cada caso espera
una página y la consulta devuelve hasta `Rag:TopK` (8) fuentes.

## Ingesta multi-fuente

```powershell
# PDF: crea el expediente y sus fragmentos
docker exec ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-ingest-pdf /work/AGENTE-DETALLE.pdf"

# API REST que devuelve {documents:[{id,title,text|pages}]}
docker exec ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-ingest-rest http://host.docker.internal:8099/doc.json"
```

Ambas fuentes se persisten como expedientes (`Documents`) y se indexan como fragmentos.
La ingesta SQL se hace con `--rag-reindex`.

## Cross-encoder real

`eval/reranker_service.py` expone un microservicio local con FastEmbed/ONNX
(`BAAI/bge-reranker-v2-m3-int8`). El pipeline lo consulta antes de generar y cae al
reranker semántico si el servicio no responde.

```powershell
python -m venv .venv-reranker
.\.venv-reranker\Scripts\python -m pip install -r eval\requirements-reranker.txt
.\.venv-reranker\Scripts\python eval\reranker_service.py --port 4098
```

- Local: `Rag__RerankerUrl=http://127.0.0.1:4098`.
- Docker: `Rag__RerankerUrl=http://host.docker.internal:4098`.
- `Rag:RerankCandidates` fija cuántos candidatos se reordenan (10 por defecto; nunca menos que `Rag:TopK` ni más de 30).
- El servicio devuelve puntuaciones en [0, 1] (sigmoide de los logits).
- Escucha solo en `127.0.0.1` por defecto. Si el contenedor no lo alcanza por `host.docker.internal`, arráncalo con `--host 0.0.0.0` y bloquea el puerto 4098 en el firewall para conexiones externas: no tiene autenticación.
- Rechaza cuerpos de más de 1 MB o de más de 100 documentos.

## DeepEval

```powershell
# Exporta pregunta, respuesta, contextos y referencia
docker exec -e Rag__Rerank=false ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval-export /work/eval/export.json"

# Instala el arnés en un entorno aislado
python -m venv .venv-ragas
.\.venv-ragas\Scripts\python -m pip install -r eval\requirements-deepeval.txt

# Ejecuta con DeepSeek como juez (30 casos).
# ATENCIÓN: envía preguntas, respuestas y contextos (texto de los expedientes) a
# api.deepseek.com. Úsalo solo con documentos de prueba; el juez local (Ollama,
# por defecto) no saca datos de la máquina.
$env:JUDGE = "deepseek"
$env:ALLOW_EXTERNAL_JUDGE = "1"     # confirmación explícita del envío externo
$env:DEEPSEEK_API_KEY = "<clave>"   # no versionar
$env:EVAL_LIMIT = "30"
.\.venv-ragas\Scripts\python eval\deepeval_eval.py eval\export.json
```

También se puede ejecutar solo una métrica:

```powershell
$env:METRICS = "faithfulness"   # o "retrieval" o "all"
```

### Resultado DeepEval (30 casos, juez DeepSeek)

| Métrica | Valor | Umbral |
|---|---:|---:|
| Context Recall | **0.876** | ≥ 0.80 |
| Context Precision | 0.770 | — |
| Faithfulness | **0.972** | ≥ 0.90 |
| Answer Relevancy | 0.311 | — |

El resultado está en `eval/deepeval-results.json`. La exportación usó respuestas extractivas
y `Rag__Rerank=false` para que la medición fuera determinista; el cross-encoder se evalúa
por separado con el evaluador proxy de 30 casos.

RAGAS no se pudo instalar en Python 3.14 porque `scikit-network` no tiene wheel; DeepEval sí.
