import os
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

for key in tuple(os.environ):
    if key.startswith(("LANGCHAIN_", "LANGSMITH_")):
        del os.environ[key]
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"

import ipaddress
import json
from urllib.parse import urlsplit

import httpx
from langchain_core.messages import AIMessage
from langchain_core.prompts import ChatPromptTemplate
from langchain_ollama import ChatOllama
from langsmith import tracing_context
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
    model: str = Field(min_length=1, max_length=200)
    timeout: int = Field(ge=1, le=600)
    question: str = Field(min_length=1, max_length=2000)
    sources: list[Source] = Field(min_length=1, max_length=6)
    system: str = Field(min_length=1, max_length=4000)

    @model_validator(mode="after")
    def validate_local(self) -> "Request":
        endpoint = urlsplit(self.base_url)
        if (
            endpoint.scheme not in ("http", "https")
            or not ipaddress.ip_address(endpoint.hostname or "").is_loopback
            or endpoint.username is not None
            or endpoint.password is not None
            or endpoint.path not in ("", "/")
            or endpoint.query
            or endpoint.fragment
            or "cloud" in self.model.lower()
        ):
            raise ValueError("Only a local Ollama endpoint and model are allowed.")
        if [source.id for source in self.sources] != list(range(1, len(self.sources) + 1)):
            raise ValueError("Source IDs must be contiguous.")
        return self


class Answer(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    answer: str = Field(min_length=1, max_length=10000)
    citations: list[int] = Field(min_length=1, max_length=6)


class Generation(Answer):
    input_tokens: int | None = Field(default=None, ge=0)
    output_tokens: int | None = Field(default=None, ge=0)


def verify_model(request: Request) -> None:
    with httpx.Client(
        base_url=request.base_url, timeout=request.timeout,
        trust_env=False, follow_redirects=False,
    ) as client:
        response = client.post("/api/show", json={"model": request.model})
        response.raise_for_status()
        details = response.json()
    if (
        details.get("remote_host")
        or details.get("remote_model")
        or details.get("details", {}).get("format") != "gguf"
        or "completion" not in details.get("capabilities", [])
    ):
        raise ValueError("The model is not a verified local GGUF completion model.")


def generate(request: Request) -> Generation:
    verify_model(request)
    model = ChatOllama(
        model=request.model, base_url=request.base_url,
        temperature=0, num_ctx=8192, num_predict=600, keep_alive="5m",
        client_kwargs={
            "timeout": request.timeout, "trust_env": False, "follow_redirects": False,
        },
    )
    prompt = ChatPromptTemplate.from_messages([
        ("system", request.system),
        ("human", "{context}"),
    ])
    chain = prompt | model.with_structured_output(
        Answer, method="json_schema", include_raw=True,
    )
    with tracing_context(enabled=False):
        result = chain.invoke(
            {"context": json.dumps({
                "question": request.question,
                "sources": [source.model_dump() for source in request.sources],
            }, ensure_ascii=False)},
            config={"callbacks": []},
        )
    raw = result["raw"]
    if not isinstance(raw, AIMessage) or raw.response_metadata.get("done_reason") != "stop":
        raise ValueError("The model did not complete its response.")
    answer = result["parsed"]
    if result["parsing_error"] is not None or not isinstance(answer, Answer):
        raise ValueError("The model returned invalid structured output.")
    if not answer.answer.strip() or any(
        citation < 1 or citation > len(request.sources) for citation in answer.citations
    ):
        raise ValueError("The model returned invalid citations or an empty answer.")
    usage = raw.usage_metadata
    return Generation(
        **answer.model_dump(),
        input_tokens=usage["input_tokens"] if usage else None,
        output_tokens=usage["output_tokens"] if usage else None,
    )


def main() -> None:
    if sys.argv[1:] == ["--check"]:
        print(json.dumps({"status": "ready", "framework": "LangChain", "provider": "ollama-local"}))
        return
    if sys.argv[1:]:
        raise ValueError("Unexpected arguments.")
    payload = sys.stdin.buffer.read(100_001)
    if len(payload) > 100_000:
        raise ValueError("Request exceeds the context limit.")
    request = Request.model_validate_json(payload)
    print(generate(request).model_dump_json())


if __name__ == "__main__":
    try:
        main()
    except (ValueError, httpx.HTTPError, ConnectionError, TimeoutError) as error:
        print(f"LangChain failed ({type(error).__name__}); no remote fallback was used.", file=sys.stderr)
        sys.exit(1)
