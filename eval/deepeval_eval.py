"""EvaluaciÃ³n DeepEval del pipeline RAG con juez local Ollama.

Uso:
  python eval/deepeval_eval.py eval/export.json
  EVAL_LIMIT=2 python eval/deepeval_eval.py eval/export.json

Requiere: deepeval y Ollama con un modelo de chat (por defecto qwen2.5:3b).
"""
from __future__ import annotations

import asyncio
import json
import os
import sys
import urllib.request

from deepeval.metrics import (
    AnswerRelevancyMetric,
    ContextualPrecisionMetric,
    ContextualRecallMetric,
    FaithfulnessMetric,
)
from deepeval.models import DeepEvalBaseLLM
from deepeval.test_case import LLMTestCase


class OllamaJudge(DeepEvalBaseLLM):
    def __init__(self, model_name: str, base_url: str) -> None:
        self.model_name = model_name
        self.base_url = base_url.rstrip("/")
        super().__init__(model=model_name)

    def load_model(self):
        return self.model_name

    def get_model_name(self) -> str:
        return self.model_name

    def generate(self, prompt: str, schema=None, **kwargs) -> str:
        if schema is not None:
            try:
                schema_json = json.dumps(schema.model_json_schema(), ensure_ascii=False)
                prompt = (prompt + "\n\nDevuelve SOLO un objeto JSON válido que cumpla este esquema "
                                   "(sin markdown):\n" + schema_json)
            except Exception:
                pass
        payload = json.dumps({
            "model": self.model_name,
            "prompt": prompt,
            "stream": False,
            "format": "json" if schema is not None else None,
            "options": {"temperature": 0},
        }).encode("utf-8")
        request = urllib.request.Request(
            f"{self.base_url}/api/generate", data=payload,
            headers={"Content-Type": "application/json"}, method="POST")
        with urllib.request.urlopen(request, timeout=600) as response:
            data = json.loads(response.read().decode("utf-8"))
        return data.get("response", "")

    async def a_generate(self, prompt: str, **kwargs) -> str:
        return await asyncio.to_thread(self.generate, prompt, **kwargs)


def main() -> int:
    path = sys.argv[1] if len(sys.argv) > 1 else "eval/export.json"
    limit = int(os.getenv("EVAL_LIMIT", "0"))
    cases = json.load(open(path, encoding="utf-8"))
    if limit > 0:
        cases = cases[:limit]
    judge = OllamaJudge(os.getenv("JUDGE_MODEL", "qwen2.5:3b"),
                        os.getenv("OLLAMA_URL", "http://127.0.0.1:11434"))
    metrics = [
        ContextualRecallMetric(threshold=0.80, model=judge, async_mode=False),
        ContextualPrecisionMetric(threshold=0.50, model=judge, async_mode=False),
        FaithfulnessMetric(threshold=0.90, model=judge, async_mode=False),
        AnswerRelevancyMetric(threshold=0.80, model=judge, async_mode=False),
    ]
    totals = [0.0] * len(metrics)
    for index, item in enumerate(cases, 1):
        test_case = LLMTestCase(
            input=item["Question"],
            actual_output=item["Answer"],
            retrieval_context=item["Contexts"],
            expected_output=item["Expected"],
        )
        values = []
        for metric in metrics:
            metric.measure(test_case)
            values.append(metric.score or 0.0)
            totals[metrics.index(metric)] += metric.score or 0.0
        print(f"[{index}/{len(cases)}] " + " ".join(
            f"{metric.__class__.__name__}={value:.3f}" for metric, value in zip(metrics, values)))
    print()
    for metric, total in zip(metrics, totals):
        print(f"{metric.__class__.__name__}: {total / len(cases):.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

