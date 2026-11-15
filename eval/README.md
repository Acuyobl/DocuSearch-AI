# Evaluación RAG

## Set de 30 preguntas

`eval/rag_questions.json` contiene 30 casos generados desde los fragmentos indexados:
pregunta, documento esperado, página y chunk. Es un set de **regresión** del corpus actual;
si cambia el corpus hay que regenerarlo.

## Evaluación .NET (sin LLM de juez)

```powershell
docker exec -e Rag__Rerank=false ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval"
```

Mide Context Recall y Context Precision por coincidencia de fuente, y Faithfulness/Answer
Relevancy con proxies léxicos. Resultado actual:

| Métrica | Valor |
|---|---:|
| Context Recall | 0.900 |
| Context Precision (proxy) | 0.154 |
| Faithfulness (proxy) | 0.966 |
| Answer Relevancy (proxy) | 0.881 |
| Casos sin fuente esperada | 3 / 30 |

Los proxies **no sustituyen DeepEval**. La precisión baja porque cada caso espera un chunk
y el endpoint devuelve hasta 8 fuentes.

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
- `Rag:RerankCandidates` limita cuántos candidatos se envían (10 por defecto).

## DeepEval

```powershell
# Exporta pregunta, respuesta, contextos y referencia
docker exec -e Rag__Rerank=false ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval-export /work/eval/export.json"

# Instala el arnés en un entorno aislado
python -m venv .venv-ragas
.\.venv-ragas\Scripts\python -m pip install -r eval\requirements-deepeval.txt

# Ejecuta con DeepSeek como juez (30 casos)
$env:JUDGE = "deepseek"
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
