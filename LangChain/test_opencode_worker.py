import json
import subprocess
import sys
import unittest
from unittest.mock import patch

from pydantic import ValidationError

import opencode_worker


def request(**overrides: object) -> opencode_worker.Request:
    payload = {
        "base_url": "http://127.0.0.1:4097",
        "password": "secreto",
        "provider_id": "opencode",
        "model_id": "free-model",
        "timeout": 10,
        "question": "What SLA?",
        "sources": [{"id": 1, "code": "TEST-001", "title": "Synthetic", "page": 1, "text": "SLA 99.99%."}],
        "system": "Use only the provided sources.",
    }
    payload.update(overrides)
    return opencode_worker.Request.model_validate(payload)


class FakeResponse:
    def __init__(self, data: object, status: int = 200) -> None:
        self._data = data
        self.status_code = status

    def raise_for_status(self) -> None:
        if self.status_code >= 400:
            raise RuntimeError(f"HTTP {self.status_code}")

    def json(self) -> object:
        return self._data


class FakeClient:
    instances: list["FakeClient"] = []

    def __init__(self, **kwargs: object) -> None:
        self.kwargs = kwargs
        self.payloads: list[dict] = []
        self.deleted = False
        FakeClient.instances.append(self)

    def __enter__(self) -> "FakeClient":
        return self

    def __exit__(self, *args: object) -> None:
        return None

    def get(self, path: str) -> FakeResponse:
        if path == "/experimental/tool/ids":
            return FakeResponse(["bash", "read"])
        raise AssertionError(path)

    def post(self, path: str, json: object = None) -> FakeResponse:
        if path == "/session":
            return FakeResponse({"id": "ses_test"})
        if path == "/session/ses_test/message":
            self.payloads.append(json)  # type: ignore[arg-type]
            return FakeResponse(
                {
                    "info": {"tokens": {"input": 3, "output": 5}},
                    "parts": [{"type": "text", "text": '{"answer":"SLA 99.99%","citations":[1]}'}],
                }
            )
        raise AssertionError(path)

    def delete(self, path: str) -> FakeResponse:
        self.deleted = path == "/session/ses_test"
        return FakeResponse({})


class OpenCodeWorkerTests(unittest.TestCase):
    def setUp(self) -> None:
        FakeClient.instances.clear()

    def test_check_mode_reports_langchain(self) -> None:
        result = subprocess.run(
            [sys.executable, "-I", opencode_worker.__file__, "--check"],
            capture_output=True, check=True, timeout=30,
        )
        payload = json.loads(result.stdout.decode("utf-8"))
        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["framework"], "LangChain")
        self.assertEqual(payload["provider"], "opencode-local")

    def test_rejects_remote_endpoint(self) -> None:
        with self.assertRaises(ValidationError):
            request(base_url="https://api.example.com")

    def test_rejects_cloud_model(self) -> None:
        with self.assertRaises(ValidationError):
            request(model_id="cloud-model")

    def test_chain_disables_tools_and_parses_answer(self) -> None:
        with patch.object(opencode_worker.httpx, "Client", FakeClient):
            result = opencode_worker.build_chain(request()).invoke(
                {"context": '{"question":"What SLA?","sources":[{"id":1,"text":"SLA 99.99%."}]}'}
            )
        self.assertEqual(result["answer"], "SLA 99.99%")
        self.assertEqual(result["citations"], [1])
        self.assertEqual(result["input_tokens"], 3)
        self.assertEqual(result["output_tokens"], 5)
        client = FakeClient.instances[-1]
        self.assertTrue(client.deleted)
        payload = client.payloads[-1]
        self.assertEqual(payload["tools"], {"bash": False, "read": False})
        self.assertEqual(payload["model"], {"providerID": "opencode", "modelID": "free-model"})
        self.assertEqual(payload["system"], "Use only the provided sources.")
        self.assertIn("SLA 99.99%", payload["parts"][0]["text"])

    def test_parse_answer_rejects_plain_text(self) -> None:
        with self.assertRaises(ValueError):
            opencode_worker.parse_answer("no json here")


if __name__ == "__main__":
    unittest.main()
