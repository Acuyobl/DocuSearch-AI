"""Pruebas deterministas del grafo LangGraph; no usan red ni modelos reales."""
from __future__ import annotations

import json
import unittest

from langchain_core.messages import AIMessage

import langgraph_worker as worker


class FakeModel:
    def __init__(self, critiques: list[bool] | None = None) -> None:
        self.critiques = list(critiques or [True])
        self.calls: list[str] = []

    def invoke(self, prompt: str):
        if "planificador" in prompt:
            self.calls.append("plan")
            return AIMessage('{"steps":["leer fuentes","redactar"]}')
        if "crítico documental" in prompt:
            self.calls.append("critique")
            ok = self.critiques.pop(0) if self.critiques else True
            if ok:
                return AIMessage('{"ok":true,"issues":[],"citations":[1]}')
            return AIMessage('{"ok":false,"issues":["falta una cita"],"citations":[]}')
        self.calls.append("synthesize")
        return AIMessage('{"answer":"Uruk fue un centro importante [1].","citations":[1]}')


def request(**overrides):
    payload = {
        "base_url": "http://127.0.0.1:11434",
        "model": "qwen2.5-coder:7b",
        "timeout": 30,
        "question": "Resume la fuente.",
        "sources": [{"id": 1, "code": "PRUEBA-1", "title": "Prueba", "page": 1,
                     "text": "Uruk fue un centro importante de Mesopotamia."}],
        "system": "Responde solo con JSON.",
        "max_sources": 1,
        "max_retries": 1,
    }
    payload.update(overrides)
    return worker.Request.model_validate(payload)


class GraphTests(unittest.TestCase):
    def test_valid_graph(self):
        model = FakeModel()
        result = worker.build_graph(model).invoke({"request": request()})
        self.assertEqual(result["final"], "Uruk fue un centro importante [1].")
        self.assertEqual(result["citations_final"], [1])
        self.assertEqual(result["steps"], ["prepare", "plan", "synthesize", "critique", "finalize"])

    def test_retry_after_critique(self):
        model = FakeModel([False, True])
        result = worker.build_graph(model).invoke({"request": request()})
        self.assertEqual(result["final"], "Uruk fue un centro importante [1].")
        self.assertEqual(model.calls.count("synthesize"), 2)

    def test_invalid_citation_fails(self):
        class BadModel(FakeModel):
            def invoke(self, prompt: str):
                if "crítico documental" in prompt:
                    return AIMessage('{"ok":true,"issues":[],"citations":[9]}')
                if "planificador" in prompt:
                    return AIMessage('{"steps":[]}')
                return AIMessage('{"answer":"Inventado [9].","citations":[9]}')

        result = worker.build_graph(BadModel()).invoke({"request": request()})
        self.assertTrue(result["error"])
        self.assertFalse(result["final"])

    def test_prepare_truncates_sources(self):
        model = FakeModel()
        result = worker.build_graph(model).invoke({"request": request(
            sources=[{"id": 1, "code": "PRUEBA-1", "title": "Prueba", "page": 1,
                      "text": "x" * 3000}], max_sources=1)})
        self.assertLessEqual(len(result["sources"][0]["text"]), worker.MAX_SOURCE_CHARS)

    def test_rejects_remote_endpoint(self):
        with self.assertRaises(Exception):
            request(base_url="http://192.168.1.4:11434")

    def test_request_is_json_serializable(self):
        self.assertIn("sources", json.loads(request().model_dump_json()))


if __name__ == "__main__":
    unittest.main()
