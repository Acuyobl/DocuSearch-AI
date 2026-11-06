"""Generación con OpenCode 1 o 2 orquestada por LangChain (sin Ollama).

LangChain se usa para la plantilla de mensajes, la cadena de Runnables y la
salida estructurada. El modelo se invoca en el servidor OpenCode local
(que a su vez usa sus proveedores gratis o locales).
"""

import ipaddress
import json
import os
import sys
import time
from urllib.parse import urlsplit

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

for key in tuple(os.environ):
    if key.startswith(("LANGCHAIN_", "LANGSMITH_")):
        del os.environ[key]
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"

import httpx
from langchain_core.output_parsers import StrOutputParser
from langchain_core.prompts import ChatPromptTemplate
from langchain_core.runnables import RunnableLambda
from pydantic import BaseModel, ConfigDict, Field, model_validator

_langfuse_client = None
_langfuse_checked = False


def get_langfuse():
    """Cliente Langfuse opcional. Solo se activa con las tres variables y nunca
    debe hacer fallar la consulta si Langfuse no está disponible."""
    global _langfuse_client, _langfuse_checked
    if _langfuse_checked:
        return _langfuse_client
    _langfuse_checked = True
    required = ("LANGFUSE_PUBLIC_KEY", "LANGFUSE_SECRET_KEY", "LANGFUSE_BASE_URL")
    if not all(os.getenv(key) for key in required):
        return None
    try:
        from langfuse import get_client

        _langfuse_client = get_client()
        return _langfuse_client
    except Exception as error:
        print(
            f"Langfuse desactivado ({type(error).__name__}); se continúa sin observabilidad externa.",
            file=sys.stderr,
        )
        return None


def flush_langfuse() -> None:
    if _langfuse_client is None:
        return
    try:
        _langfuse_client.flush()
    except Exception:
        pass


class Source(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    id: int = Field(ge=1, le=6)
    code: str = Field(max_length=200)
    title: str = Field(max_length=1000)
    page: int = Field(ge=1)
    text: str = Field(max_length=1001)


class Request(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    base_url: str
    username: str = "opencode"
    password: str = ""
    provider_id: str = Field(min_length=1, max_length=200)
    model_id: str = Field(min_length=1, max_length=200)
    agent: str = Field(max_length=200, default="")
    timeout: int = Field(ge=1, le=600)
    question: str = Field(min_length=1, max_length=2000)
    sources: list[Source] = Field(min_length=1, max_length=6)
    system: str = Field(min_length=1, max_length=4000)

    @model_validator(mode="after")
    def validate_local(self) -> "Request":
        endpoint = urlsplit(self.base_url)
        host = endpoint.hostname or ""
        loopback = host == "localhost" or (
            _is_ip(host) and ipaddress.ip_address(host).is_loopback
        )
        container_host = host == "host.docker.internal"
        if (
            endpoint.scheme not in ("http", "https")
            or not (loopback or container_host)
            or endpoint.username is not None
            or endpoint.password is not None
            or endpoint.path not in ("", "/")
            or endpoint.query
            or endpoint.fragment
            or (container_host and not self.password)
        ):
            raise ValueError("Only a local/container OpenCode endpoint with no embedded credentials is allowed.")
        if "cloud" in self.model_id.lower():
            raise ValueError("Cloud models are not allowed in this worker.")
        return self


class Answer(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    answer: str = Field(min_length=1, max_length=10000)
    citations: list[int] = Field(min_length=1, max_length=6)


def _is_ip(value: str) -> bool:
    try:
        ipaddress.ip_address(value)
        return True
    except ValueError:
        return False


def _message_parts(prompt_value, system: str) -> tuple[str, str]:
    messages = prompt_value.to_messages()
    system_text = "\n\n".join(
        message.content for message in messages if message.type == "system"
    ) or system
    human = "\n\n".join(message.content for message in messages if message.type == "human")
    return system_text, human


def _http_client(request: Request) -> httpx.Client:
    auth = (request.username, request.password) if request.password else None
    return httpx.Client(
        base_url=request.base_url,
        timeout=request.timeout,
        trust_env=False,
        follow_redirects=False,
        auth=auth,
    )


def _is_open_code_2(client: httpx.Client) -> bool:
    try:
        response = client.get("/api/info")
        if response.status_code != 200:
            return False
        data = response.json()
        return isinstance(data, dict) and ("version" in data or "urls" in data)
    except Exception:
        # En OpenCode 1 esta ruta no existe (404/HTML) y se usa la API clásica.
        return False


def call_opencode(request: Request, prompt_value) -> tuple[str, dict]:
    system, human = _message_parts(prompt_value, request.system)
    with _http_client(request) as client:
        if _is_open_code_2(client):
            return _call_open_code_2(client, request, system, human)
        return _call_open_code_1(client, request, system, human)


def _call_open_code_1(
    client: httpx.Client, request: Request, system: str, human: str
) -> tuple[str, dict]:
    tool_ids = client.get("/experimental/tool/ids")
    tool_ids.raise_for_status()
    tool_list = tool_ids.json()
    if isinstance(tool_list, dict):
        tool_list = tool_list.get("value", [])
    if not isinstance(tool_list, list) or not all(isinstance(item, str) for item in tool_list):
        raise ValueError("OpenCode returned an unexpected tool id list.")
    session = client.post("/session", json={"title": "DocuSearch LangChain"})
    session.raise_for_status()
    session_id = session.json().get("id", "")
    if not session_id:
        raise ValueError("OpenCode did not return a session id.")
    try:
        payload = {
            "model": {"providerID": request.provider_id, "modelID": request.model_id},
            "system": system,
            "tools": {tool: False for tool in tool_list},
            "parts": [{"type": "text", "text": human}],
        }
        if request.agent:
            payload["agent"] = request.agent
        response = client.post(f"/session/{session_id}/message", json=payload)
        response.raise_for_status()
        data = response.json()
        text = "".join(
            part.get("text", "") for part in data.get("parts", []) if part.get("type") == "text"
        )
        tokens = (data.get("info") or {}).get("tokens") or {}
        return text, tokens
    finally:
        try:
            client.delete(f"/session/{session_id}")
        except httpx.HTTPError:
            pass


def _call_open_code_2(
    client: httpx.Client, request: Request, system: str, human: str
) -> tuple[str, dict]:
    body: dict = {
        "title": "DocuSearch LangChain",
        "model": {"id": request.model_id, "providerID": request.provider_id},
        "permissions": [{"action": "*", "resource": "*", "effect": "deny"}],
    }
    if request.agent:
        body["agent"] = request.agent
    session = client.post("/api/session", json=body)
    session.raise_for_status()
    session_data = (session.json().get("data") or {})
    session_id = session_data.get("id", "")
    if not session_id:
        raise ValueError("OpenCode 2 did not return a session id.")
    try:
        prompt = client.post(
            f"/api/session/{session_id}/prompt",
            json={"text": system + "\n\n" + human},
        )
        prompt.raise_for_status()
        deadline = time.monotonic() + request.timeout
        while time.monotonic() < deadline:
            time.sleep(0.5)
            messages = client.get(
                f"/api/session/{session_id}/message",
                params={"order": "asc", "limit": 100},
            )
            messages.raise_for_status()
            items = messages.json().get("data") or []
            assistant = [item for item in items if item.get("type") == "assistant"]
            if not assistant:
                continue
            last = assistant[-1]
            error = last.get("error")
            if isinstance(error, dict) and error.get("message"):
                raise ValueError("OpenCode 2: " + str(error["message"]))
            text = "".join(
                part.get("text", "")
                for part in (last.get("content") or [])
                if part.get("type") == "text"
            )
            finished = bool(last.get("finish")) or bool((last.get("time") or {}).get("completed"))
            if text and finished:
                return text, last.get("tokens") or {}
        raise ValueError("OpenCode 2 did not finish the request in time.")
    finally:
        try:
            client.delete(f"/api/session/{session_id}")
        except httpx.HTTPError:
            pass


def parse_answer(text: str) -> Answer:
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        raise ValueError("The model did not return a JSON object.")
    return Answer.model_validate(json.loads(text[start : end + 1]))


def build_chain(request: Request):
    usage = {"input": None, "output": None}

    def record_usage(tokens: dict) -> None:
        usage["input"] = tokens.get("input")
        usage["output"] = tokens.get("output")

    def invoke_direct(prompt_value):
        text, tokens = call_opencode(request, prompt_value)
        record_usage(tokens)
        return text

    def invoke_model(prompt_value):
        client = get_langfuse()
        if client is None:
            return invoke_direct(prompt_value)

        called = False
        text = None
        try:
            with client.start_as_current_observation(
                as_type="generation",
                name="opencode-generation",
                model=request.model_id,
                input={
                    "question_length": len(request.question),
                    "source_count": len(request.sources),
                    "provider": request.provider_id,
                },
            ) as generation:
                called = True
                text, tokens = call_opencode(request, prompt_value)
                record_usage(tokens)
                try:
                    generation.update(
                        output={"answer_length": len(text)},
                        metadata={
                            "engine": "opencode",
                            "langchain": True,
                            "provider": request.provider_id,
                            "model": request.model_id,
                            "source_count": len(request.sources),
                            "input_tokens": tokens.get("input"),
                            "output_tokens": tokens.get("output"),
                        },
                    )
                except Exception:
                    pass
            return text
        except Exception:
            # Si el fallo fue de Langfuse antes de invocar al modelo, se continúa
            # sin observabilidad. Si el modelo ya falló, se propaga el error real.
            if called and text is None:
                raise
            if text is not None:
                return text
            print(
                "Langfuse no disponible; se continúa con la consulta sin observabilidad externa.",
                file=sys.stderr,
            )
            return invoke_direct(prompt_value)

    def finish(text: str) -> dict:
        answer = parse_answer(text)
        return {
            "answer": answer.answer,
            "citations": answer.citations,
            "input_tokens": usage["input"],
            "output_tokens": usage["output"],
        }

    system_template = request.system.replace("{", "{{").replace("}", "}}")
    prompt = ChatPromptTemplate.from_messages(
        [("system", system_template), ("human", "{context}")]
    )
    return prompt | RunnableLambda(invoke_model) | StrOutputParser() | RunnableLambda(finish)


def main() -> None:
    if sys.argv[1:] == ["--check"]:
        print(json.dumps({"status": "ready", "framework": "LangChain", "provider": "opencode-local"}))
        return
    if sys.argv[1:]:
        raise ValueError("Unexpected arguments.")
    payload = sys.stdin.buffer.read(100_001)
    if len(payload) > 100_000:
        raise ValueError("Request exceeds the context limit.")
    request = Request.model_validate_json(payload)
    context = json.dumps(
        {"question": request.question, "sources": [source.model_dump() for source in request.sources]},
        ensure_ascii=False,
    )
    result = build_chain(request).invoke({"context": context})
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, httpx.HTTPError, ConnectionError, TimeoutError) as error:
        print(f"LangChain/OpenCode failed ({type(error).__name__}); no fallback was used.", file=sys.stderr)
        sys.exit(1)
    finally:
        flush_langfuse()
