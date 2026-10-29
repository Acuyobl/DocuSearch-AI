import json
import os
import subprocess
import sys
import unittest
from unittest.mock import Mock, patch

from langchain_core.messages import AIMessage
from langchain_core.runnables import RunnableLambda
from pydantic import ValidationError

import worker


def request(**overrides: object) -> worker.Request:
    payload = {
        "base_url": "http://127.0.0.1:11434",
        "model": "qwen2.5-coder:7b",
        "timeout": 10,
        "question": "What SLA?",
        "sources": [{"id": 1, "code": "TEST-001", "title": "Synthetic", "page": 1, "text": "SLA 99.99%."}],
        "system": "Use only the provided sources.",
    }
    payload.update(overrides)
    return worker.Request.model_validate(payload)


class WorkerTests(unittest.TestCase):
    def test_stdout_is_utf8_even_with_windows_code_page(self) -> None:
        code = (
            "import runpy,sys;sys.stdout.reconfigure(encoding='cp1252');"
            f"runpy.run_path({worker.__file__!r},run_name='worker');"
            "print('\\u00e1\\u00e9\\u00ed\\u00f3\\u00fa\\u00f1')"
        )
        result = subprocess.run(
            [sys.executable, "-I", "-c", code],
            capture_output=True, check=True, timeout=30,
        )
        self.assertEqual(result.stdout.decode("utf-8").strip(), "\u00e1\u00e9\u00ed\u00f3\u00fa\u00f1")

    def test_rejects_remote_endpoint_credentials_and_cloud(self) -> None:
        for url in ["https://ollama.com", "http://192.168.1.1:11434", "http://localhost:11434",
                    "http://u:p@127.0.0.1:11434", "http://127.0.0.1:11434/path"]:
            with self.subTest(url=url), self.assertRaises(ValidationError):
                request(base_url=url)
        with self.assertRaises(ValidationError):
            request(model="qwen:cloud")

    def test_bounds_and_source_ids(self) -> None:
        source = request().sources[0].model_dump()
        for changes in [
            {"question": ""}, {"question": "x" * 2001}, {"sources": []},
            {"sources": [source] * 7}, {"sources": [{**source, "text": "x" * 1002}]},
            {"sources": [{**source, "id": 2}]}, {"timeout": 601},
        ]:
            with self.subTest(changes=changes.keys()), self.assertRaises(ValidationError):
                request(**changes)

    def test_model_metadata_verified_before_context(self) -> None:
        for metadata in [
            {"details": {"format": "gguf"}, "capabilities": ["completion"], "remote_host": "https://ollama.com"},
            {"details": {"format": "gguf"}, "capabilities": ["completion"], "remote_model": "remote"},
            {"details": {"format": ""}, "capabilities": ["completion"]},
            {"details": {"format": "gguf"}, "capabilities": ["embedding"]},
        ]:
            response = Mock()
            response.json.return_value = metadata
            with patch.object(worker.httpx, "Client") as client:
                client.return_value.__enter__.return_value.post.return_value = response
                with patch.object(worker, "ChatOllama") as model, self.assertRaises(ValueError):
                    worker.generate(request())
                model.assert_not_called()
                self.assertFalse(client.call_args.kwargs["trust_env"])
                self.assertFalse(client.call_args.kwargs["follow_redirects"])

    def run_chain(self, answer: worker.Answer | None, reason: str = "stop") -> worker.Answer:
        def invoke(prompt: object) -> dict:
            self.assertIn("TEST-001", str(prompt))
            self.assertIn("SLA 99.99%", str(prompt))
            return {"raw": AIMessage(content="{}", response_metadata={"done_reason": reason},
                                     usage_metadata={"input_tokens": 123, "output_tokens": 45, "total_tokens": 168}),
                    "parsed": answer, "parsing_error": None}
        with patch.object(worker, "verify_model"), patch.object(worker, "ChatOllama") as model:
            model.return_value.with_structured_output.return_value = RunnableLambda(invoke)
            result = worker.generate(request())
            self.assertEqual(model.return_value.with_structured_output.call_args.kwargs["method"], "json_schema")
            self.assertFalse(model.call_args.kwargs["client_kwargs"]["trust_env"])
            return result

    def test_real_langchain_composition_with_mock_model(self) -> None:
        self.assertEqual(self.run_chain(worker.Answer(answer="99.99%", citations=[1])).citations, [1])

    def test_usage_comes_from_model_metadata_not_generated_answer(self) -> None:
        result = self.run_chain(worker.Answer(answer="99.99%", citations=[1]))
        self.assertEqual((result.input_tokens, result.output_tokens), (123, 45))
        self.assertNotIn("input_tokens", worker.Answer.model_json_schema()["properties"])
        self.assertNotIn("output_tokens", worker.Answer.model_json_schema()["properties"])

    def test_invalid_citations_truncation_and_parser_failure(self) -> None:
        for answer, reason in [
            (worker.Answer(answer="Text", citations=[2]), "stop"),
            (worker.Answer(answer="Text", citations=[0]), "stop"),
            (worker.Answer(answer=" ", citations=[1]), "stop"),
            (worker.Answer(answer="Text", citations=[1]), "length"),
            (None, "stop"),
        ]:
            with self.subTest(reason=reason), self.assertRaises(ValueError):
                self.run_chain(answer, reason)

    def test_tracing_disabled(self) -> None:
        self.assertEqual(os.environ["LANGSMITH_TRACING"], "false")
        self.assertEqual(os.environ["LANGCHAIN_TRACING_V2"], "false")
        self.assertNotIn("LANGSMITH_API_KEY", os.environ)


if __name__ == "__main__":
    unittest.main()
