"""Generación con OpenCode orquestada por LangChain (sin Ollama).

LangChain se usa para la plantilla de mensajes, la cadena de Runnables y la
salida estructurada. El modelo se invoca en el servidor OpenCode local
(que a su vez usa sus proveedores gratis o locales).
"""

import ipaddress
import json
import os
import sys
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


def call_opencode(request: Request, prompt_value) -> tuple[str, dict]:
    messages = prompt_value.to_messages()
    system = "\n\n".join(message.content for message in messages if message.type == "system") or request.system
    human = "\n\n".join(message.content for message in messages if message.type == "human")
    auth = (request.username, request.password) if request.password else None
    with httpx.Client(
        base_url=request.base_url,
        timeout=request.timeout,
        trust_env=False,
        follow_redirects=False,
        auth=auth,
    ) as client:
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


def parse_answer(text: str) -> Answer:
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        raise ValueError("The model did not return a JSON object.")
    return Answer.model_validate(json.loads(text[start : end + 1]))


def build_chain(request: Request):
    usage = {"input": None, "output": None}

    def invoke_model(prompt_value):
        text, tokens = call_opencode(request, prompt_value)
        usage["input"] = tokens.get("input")
        usage["output"] = tokens.get("output")
        return text

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
