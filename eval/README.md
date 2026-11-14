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

Los proxies **no sustituyen RAGAS/DeepEval**. La precisión baja porque cada caso espera un
chunk y el endpoint devuelve hasta 8 fuentes; RAGAS la evalúa con un juez.

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

La evaluación de 30 casos con el cross-encoder mantiene: Recall 0.900, Precision 0.154,
Faithfulness 0.966 y Relevancy 0.881.

## DeepEval

```powershell
# Exporta pregunta, respuesta, contextos y referencia
docker exec -e Rag__Rerank=false ds-app bash -lc "cd /work && dotnet run --no-build --project DocuSearch.csproj -- --rag-eval-export /work/eval/export.json"

# Instala el arnés en un entorno aislado
python -m venv .venv-ragas
.\.venv-ragas\Scripts\python -m pip install -r eval\requirements-deepeval.txt

# Ejecuta (limita con EVAL_LIMIT para pruebas)
$env:EVAL_LIMIT = "2"
.\.venv-ragas\Scripts\python eval\deepeval_eval.py eval\export.json
```

El arnés usa `qwen2.5:3b` como juez local. En este equipo una pasada completa de 30 casos no
terminó en un tiempo razonable con CPU para las cuatro métricas; hay que ejecutarla en un
equipo con más capacidad o con un juez remoto. **RAGAS no se pudo instalar en Python 3.14**
porque `scikit-network` no tiene wheel; DeepEval sí está instalado en el entorno de prueba.
