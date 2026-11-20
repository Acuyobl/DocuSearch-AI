"""Evaluación DeepEval del pipeline RAG.

Juez OpenCode 2 o DeepSeek:
  python eval/deepeval_eval.py eval/export.json
  JUDGE=opencode OPENCODE_PASSWORD=... python eval/deepeval_eval.py eval/export.json
  JUDGE=deepseek ALLOW_EXTERNAL_JUDGE=1 DEEPSEEK_API_KEY=... python eval/deepeval_eval.py eval/export.json
  EVAL_LIMIT=2 ... (para prueba)

El juez DeepSeek envía preguntas, respuestas y contextos (texto de los expedientes)
a una API externa. Por eso exige ALLOW_EXTERNAL_JUDGE=1 y debe usarse solo con
documentos de prueba, nunca con expedientes reales.
"""
from __future__ import annotations

import asyncio
import base64
import json
import os
import sys
import time
import urllib.request

from deepeval.metrics import (
    AnswerRelevancyMetric,
    ContextualPrecisionMetric,
    ContextualRecallMetric,
    FaithfulnessMetric,
)
from deepeval.models import DeepEvalBaseLLM
from deepeval.test_case import LLMTestCase



class OpenCodeJudge(DeepEvalBaseLLM):
    def __init__(self, base_url: str, password: str, provider: str, model: str) -> None:
        self.base_url = base_url.rstrip("/")
        self.password = password
        self.provider = provider
        self.model_name = model
        super().__init__(model=f"{provider}/{model}")

    def load_model(self):
        return self.model_name

    def get_model_name(self) -> str:
        return f"{self.provider}/{self.model_name}"

    def _request(self, method: str, path: str, payload: dict | None = None):
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        auth = base64.b64encode(f"opencode:{self.password}".encode()).decode()
        request = urllib.request.Request(f"{self.base_url}{path}", data=data, method=method,
                                         headers={"Content-Type": "application/json",
                                                  "Authorization": f"Basic {auth}"})
        with urllib.request.urlopen(request, timeout=600) as response:
            return json.loads(response.read().decode("utf-8"))

    def generate(self, prompt: str, schema=None, **kwargs) -> str:
        if schema is not None:
            try:
                prompt += ("\n\nDevuelve SOLO un objeto JSON válido que cumpla este esquema "
                           "(sin markdown):\n" + json.dumps(schema.model_json_schema(), ensure_ascii=False))
            except Exception:
                pass
        session = self._request("POST", "/api/session", {
            "title": "DeepEval RAG judge",
            "model": {"id": self.model_name, "providerID": self.provider},
            "permissions": [{"action": "*", "resource": "*", "effect": "deny"}],
        })
        session_id = (session.get("data") or {}).get("id", "")
        if not session_id:
            raise RuntimeError("OpenCode no devolvió sesión")
        try:
            self._request("POST", f"/api/session/{session_id}/prompt", {"text": prompt})
            deadline = time.time() + 300
            answer = ""
            while time.time() < deadline:
                time.sleep(0.5)
                messages = self._request("GET", f"/api/session/{session_id}/message?order=asc&limit=100")
                assistant = [item for item in messages.get("data", [])
                             if item.get("type") == "assistant"]
                if not assistant:
                    continue
                last = assistant[-1]
                answer = "".join(part.get("text", "") for part in last.get("content", [])
                                 if part.get("type") == "text")
                finished = bool(last.get("finish")) or bool((last.get("time") or {}).get("completed"))
                if answer and finished:
                    return answer
            raise TimeoutError("OpenCode judge agotó el tiempo")
        finally:
            try:
                self._request("DELETE", f"/api/session/{session_id}")
            except Exception:
                pass

    async def a_generate(self, prompt: str, **kwargs) -> str:
        return await asyncio.to_thread(self.generate, prompt, **kwargs)


class DeepSeekJudge(DeepEvalBaseLLM):
    def __init__(self, api_key: str, model: str = "deepseek-chat") -> None:
        self.api_key = api_key
        self.model_name = model
        self.base_url = os.getenv("DEEPSEEK_URL", "https://api.deepseek.com")
        super().__init__(model=model)

    def load_model(self):
        return self.model_name

    def get_model_name(self) -> str:
        return self.model_name

    def generate(self, prompt: str, schema=None, **kwargs) -> str:
        if schema is not None:
            try:
                prompt += ("\n\nDevuelve SOLO un objeto JSON válido que cumpla este esquema "
                           "(sin markdown):\n" + json.dumps(schema.model_json_schema(), ensure_ascii=False))
            except Exception:
                pass
        payload = json.dumps({
            "model": self.model_name,
            "messages": [{"role": "user", "content": prompt}],
            "temperature": 0,
            "max_tokens": 4096,
            **({"response_format": {"type": "json_object"}} if schema is not None else {}),
        }).encode("utf-8")
        request = urllib.request.Request(f"{self.base_url}/chat/completions", data=payload,
                                         headers={"Content-Type": "application/json",
                                                  "Authorization": f"Bearer {self.api_key}"},
                                         method="POST")
        with urllib.request.urlopen(request, timeout=300) as response:
            data = json.loads(response.read().decode("utf-8"))
        return data["choices"][0]["message"]["content"]

    async def a_generate(self, prompt: str, **kwargs) -> str:
        return await asyncio.to_thread(self.generate, prompt, **kwargs)


def main() -> int:
    path = sys.argv[1] if len(sys.argv) > 1 else "eval/export.json"
    limit = int(os.getenv("EVAL_LIMIT", "0"))
    cases = json.load(open(path, encoding="utf-8"))
    if limit > 0:
        cases = cases[:limit]
    if os.getenv("JUDGE", "opencode").lower() == "deepseek":
        if os.getenv("ALLOW_EXTERNAL_JUDGE") != "1":
            raise SystemExit("JUDGE=deepseek envía el texto de los expedientes a una API externa. "
                             "Define ALLOW_EXTERNAL_JUDGE=1 para confirmarlo (solo con datos de prueba).")
        api_key = os.getenv("DEEPSEEK_API_KEY", "")
        if not api_key:
            raise SystemExit("DEEPSEEK_API_KEY no está definida")
        print(f"AVISO: {len(cases)} casos (preguntas, respuestas y contextos) se enviarán a "
              f"{os.getenv('DEEPSEEK_URL', 'https://api.deepseek.com')}.", file=sys.stderr, flush=True)
        judge = DeepSeekJudge(api_key, os.getenv("DEEPSEEK_MODEL", "deepseek-chat"))
    elif os.getenv("JUDGE", "opencode").lower() == "opencode":
        judge = OpenCodeJudge(
            os.getenv("OPENCODE_URL", "http://127.0.0.1:4097"),
            os.getenv("OPENCODE_PASSWORD", "DocuSearch-Local-2026"),
            os.getenv("OPENCODE_PROVIDER", "deepseek"),
            os.getenv("OPENCODE_MODEL", "deepseek-flash"))
    else:
        raise SystemExit("JUDGE debe ser opencode o deepseek")
    selected = os.getenv("METRICS", "all").lower()
    if selected == "faithfulness":
        metrics = [FaithfulnessMetric(threshold=0.90, model=judge, async_mode=False)]
    elif selected == "retrieval":
        metrics = [
            ContextualRecallMetric(threshold=0.80, model=judge, async_mode=False),
            ContextualPrecisionMetric(threshold=0.50, model=judge, async_mode=False),
        ]
    else:
        metrics = [
            ContextualRecallMetric(threshold=0.80, model=judge, async_mode=False),
            ContextualPrecisionMetric(threshold=0.50, model=judge, async_mode=False),
            FaithfulnessMetric(threshold=0.90, model=judge, async_mode=False),
            AnswerRelevancyMetric(threshold=0.80, model=judge, async_mode=False),
        ]
    totals = [0.0] * len(metrics)
    for index, item in enumerate(cases, 1):
        test_case = LLMTestCase(input=item["Question"], actual_output=item["Answer"],
                                retrieval_context=item["Contexts"],
                                expected_output=item["Expected"])
        values = []
        for position, metric in enumerate(metrics):
            metric.measure(test_case)
            values.append(metric.score or 0.0)
            totals[position] += metric.score or 0.0
        print(f"[{index}/{len(cases)}] " + " ".join(
            f"{metric.__class__.__name__}={value:.3f}" for metric, value in zip(metrics, values)),
            flush=True)
    print()
    for metric, total in zip(metrics, totals):
        print(f"{metric.__class__.__name__}: {total / len(cases):.3f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

