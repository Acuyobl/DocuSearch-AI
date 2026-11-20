"""Flujo LangGraph acotado para síntesis/análisis sobre fuentes ya autorizadas.

El worker NO recupera, NO escribe, NO usa herramientas y NO accede a la base de datos.
Recibe fuentes ya filtradas por C#, produce un borrador, lo critica una vez y devuelve
JSON estructurado. C# vuelve a validar las citas con AG-02 antes de mostrarlas.

Uso normal: recibe un JSON por stdin desde Services/LangGraphRunner.cs.
Comprobación: python langgraph_worker.py --check
"""

from __future__ import annotations

import ipaddress
import json
import os
import sys
import time
from typing import Any, TypedDict
from urllib.parse import urlsplit

import httpx

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

for key in tuple(os.environ):
    if key.startswith(("LANGCHAIN_", "LANGSMITH_")):
        del os.environ[key]
os.environ["LANGSMITH_TRACING"] = "false"
os.environ["LANGCHAIN_TRACING_V2"] = "false"

from langchain_core.messages import AIMessage  # noqa: E402
from langgraph.graph import END, START, StateGraph  # noqa: E402
from pydantic import BaseModel, ConfigDict, Field, model_validator  # noqa: E402

MAX_REQUEST_BYTES = 120_000
MAX_SOURCE_CHARS = 1_500
MAX_ANSWER_CHARS = 10_000


class Source(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    id: int = Field(ge=1, le=20)
    code: str = Field(max_length=200)
    title: str = Field(max_length=1000)
    page: int = Field(ge=1)
    text: str = Field(min_length=1, max_length=3000)


class Request(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)
    base_url: str
    username: str = Field(default="opencode", max_length=200)
    password: str = Field(default="", max_length=500)
    provider_id: str = Field(min_length=1, max_length=200)
    model_id: str = Field(min_length=1, max_length=200)
    agent: str = Field(default="", max_length=200)
    timeout: int = Field(ge=5, le=600)
    question: str = Field(min_length=1, max_length=2000)
    sources: list[Source] = Field(min_length=1, max_length=6)
    system: str = Field(min_length=1, max_length=4000)
    max_sources: int = Field(default=6, ge=1, le=6)
    max_retries: int = Field(default=1, ge=0, le=2)

    @model_validator(mode="after")
    def validate(self) -> "Request":
        endpoint = urlsplit(self.base_url)
        host = (endpoint.hostname or "").lower()
        local = host in {"127.0.0.1", "localhost", "::1", "host.docker.internal"}
        if (
            endpoint.scheme not in ("http", "https")
            or not local
            or endpoint.username is not None
            or endpoint.password is not None
            or endpoint.path not in ("", "/")
            or endpoint.query
            or endpoint.fragment
        ):
            raise ValueError("Only a local OpenCode endpoint is allowed.")
        if [source.id for source in self.sources] != list(range(1, len(self.sources) + 1)):
            raise ValueError("Source IDs must be contiguous.")
        return self


class OpenCodeChat:
    """Adaptador mínimo al servidor OpenCode 2; no expone herramientas al modelo."""

    def __init__(self, request: Request) -> None:
        self.request = request
        self.auth = (request.username, request.password) if request.password else None

    def invoke(self, prompt: str) -> AIMessage:
        request = self.request
        with httpx.Client(
            base_url=request.base_url, auth=self.auth, timeout=request.timeout,
            trust_env=False, follow_redirects=False,
        ) as client:
            body: dict[str, Any] = {
                "title": "DocuSearch LangGraph",
                "model": {"id": request.model_id, "providerID": request.provider_id},
                "permissions": [{"action": "*", "resource": "*", "effect": "deny"}],
            }
            if request.agent:
                body["agent"] = request.agent
            created = client.post("/api/session", json=body)
            created.raise_for_status()
            session_id = (created.json().get("data") or {}).get("id")
            if not session_id:
                raise ValueError("OpenCode no devolvió una sesión.")
            try:
                prompted = client.post(f"/api/session/{session_id}/prompt", json={"text": prompt})
                prompted.raise_for_status()
                deadline = time.monotonic() + request.timeout
                while time.monotonic() < deadline:
                    time.sleep(0.4)
                    messages = client.get(
                        f"/api/session/{session_id}/message?order=asc&limit=100")
                    messages.raise_for_status()
                    assistant = [item for item in messages.json().get("data", [])
                                 if item.get("type") == "assistant"]
                    if not assistant:
                        continue
                    last = assistant[-1]
                    text = "".join(part.get("text", "") for part in last.get("content", [])
                                   if part.get("type") == "text" and part.get("text"))
                    finished = bool(last.get("finish")) or bool((last.get("time") or {}).get("completed"))
                    if text and finished:
                        return AIMessage(text)
                raise TimeoutError("OpenCode no terminó dentro del tiempo configurado.")
            finally:
                try:
                    client.delete(f"/api/session/{session_id}")
                except httpx.HTTPError:
                    pass


class GraphState(TypedDict, total=False):
    request: Request
    question: str
    sources: list[dict[str, Any]]
    plan: list[str]
    draft: str
    citations: list[int]
    critique: str
    critique_ok: bool
    final: str
    citations_final: list[int]
    steps: list[str]
    model_calls: int
    error: str


def _text(message: Any) -> str:
    content = getattr(message, "content", message)
    if isinstance(content, list):
        return "".join(str(part.get("text", "")) if isinstance(part, dict) else str(part) for part in content)
    return str(content)


def _json_object(text: str) -> dict[str, Any]:
    start = text.find("{")
    end = text.rfind("}")
    if start < 0 or end <= start:
        raise ValueError("El modelo no devolvió JSON.")
    value = json.loads(text[start:end + 1])
    if not isinstance(value, dict):
        raise ValueError("El modelo no devolvió un objeto JSON.")
    return value


def _valid_citations(value: Any, count: int) -> list[int]:
    if not isinstance(value, list) or not value:
        raise ValueError("El modelo no devolvió citas.")
    result: list[int] = []
    for item in value:
        if isinstance(item, bool) or not isinstance(item, int) or item < 1 or item > count:
            raise ValueError("El modelo devolvió una cita fuera de las fuentes.")
        if item not in result:
            result.append(item)
    return result


def build_graph(model: Any):
    """Construye el grafo con un modelo inyectable (permite pruebas con fake LLM)."""

    def prepare(state: GraphState) -> dict[str, Any]:
        request = state["request"]
        sources = []
        for source in request.sources[: request.max_sources]:
            sources.append({
                "id": source.id,
                "code": source.code,
                "title": source.title,
                "page": source.page,
                "text": source.text[:MAX_SOURCE_CHARS],
            })
        return {"sources": sources, "question": request.question, "steps": ["prepare"], "model_calls": 0}

    def plan(state: GraphState) -> dict[str, Any]:
        # El plan es determinista para que el MVP no gaste una llamada extra:
        # la mejora frente al flujo anterior es el estado explícito y la crítica/reintento.
        return {"plan": ["identificar los datos relevantes", "redactar una respuesta con citas"],
                "steps": state.get("steps", []) + ["plan"]}

    def synthesize(state: GraphState) -> dict[str, Any]:
        request = state["request"]
        prompt = (
            f"{request.system}\n\n"
            "Devuelve SOLO JSON con {\"answer\": \"...\", \"citations\": [1, 2]}. "
            "No inventes documentos, páginas ni cifras. Si no consta, dilo.\n"
            f"Plan: {json.dumps(state.get('plan', []), ensure_ascii=False)}\n"
            f"Pregunta: {request.question}\n"
            f"Fuentes: {json.dumps(state['sources'], ensure_ascii=False)}"
        )
        try:
            parsed = _json_object(_text(model.invoke(prompt)))
            answer = str(parsed.get("answer", "")).strip()
            citations = _valid_citations(parsed.get("citations"), len(state["sources"]))
            if not answer or len(answer) > MAX_ANSWER_CHARS:
                raise ValueError("Respuesta vacía o demasiado larga.")
            return {"draft": answer, "citations": citations,
                    "steps": state.get("steps", []) + ["synthesize"],
                    "model_calls": state.get("model_calls", 0) + 1}
        except Exception as error:
            return {"draft": "", "citations": [], "critique_ok": False, "error": str(error),
                    "steps": state.get("steps", []) + ["synthesize"],
                    "model_calls": state.get("model_calls", 0) + 1}

    def critique(state: GraphState) -> dict[str, Any]:
        # Crítica determinista: comprueba que haya borrador y citas dentro del conjunto.
        # No gasta una segunda llamada al modelo; el resultado sigue pasando por AG-02 en C#.
        try:
            clean = _valid_citations(state.get("citations"), len(state["sources"]))
            ok = bool(state.get("draft")) and bool(clean)
            return {"critique_ok": ok, "critique": "ok" if ok else "sin borrador o citas válidas",
                    "citations": clean, "steps": state.get("steps", []) + ["critique"]}
        except ValueError as error:
            return {"critique_ok": False, "critique": str(error)[:300],
                    "steps": state.get("steps", []) + ["critique"]}

    def route(state: GraphState) -> str:
        attempts = state.get("steps", []).count("synthesize")
        return "synthesize" if not state.get("critique_ok") and attempts <= state["request"].max_retries else "finalize"

    def finalize(state: GraphState) -> dict[str, Any]:
        draft = state.get("draft", "").strip()
        citations = state.get("citations") or state.get("citations_final") or []
        if not draft or not citations:
            return {"final": "", "error": state.get("error") or "El grafo no produjo una respuesta válida.",
                    "steps": state.get("steps", []) + ["finalize"]}
        try:
            clean = _valid_citations(citations, len(state["sources"]))
        except ValueError as error:
            return {"final": "", "error": str(error), "steps": state.get("steps", []) + ["finalize"]}
        return {"final": draft, "citations_final": clean, "error": None,
                "steps": state.get("steps", []) + ["finalize"]}

    graph = StateGraph(GraphState)
    graph.add_node("prepare", prepare)
    graph.add_node("plan", plan)
    graph.add_node("synthesize", synthesize)
    graph.add_node("critique", critique)
    graph.add_node("finalize", finalize)
    graph.add_edge(START, "prepare")
    graph.add_edge("prepare", "plan")
    graph.add_edge("plan", "synthesize")
    graph.add_edge("synthesize", "critique")
    graph.add_conditional_edges("critique", route, {"synthesize": "synthesize", "finalize": "finalize"})
    graph.add_edge("finalize", END)
    return graph.compile()


def run(request: Request) -> dict[str, Any]:
    result = build_graph(OpenCodeChat(request)).invoke(
        {"request": request}, {"recursion_limit": 12, "configurable": {}},
    )
    if "error" in result and result["error"]:
        raise ValueError(result["error"])
    if not result.get("final"):
        raise ValueError("El grafo no produjo una respuesta válida.")
    return {
        "status": "ok",
        "answer": result["final"],
        "citations": result.get("citations_final", []),
        "steps": result.get("steps", []),
        "usage": {"model_calls": result.get("model_calls", 0)},
    }


def main() -> None:
    if sys.argv[1:] == ["--check"]:
        print(json.dumps({"status": "ready", "framework": "LangGraph", "provider": "opencode-2"}))
        return
    if sys.argv[1:]:
        raise ValueError("Unexpected arguments.")
    payload = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1)
    if len(payload) > MAX_REQUEST_BYTES:
        raise ValueError("Request exceeds the context limit.")
    request = Request.model_validate_json(payload)
    print(json.dumps(run(request), ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"LangGraph failed ({type(error).__name__}); no remote fallback was used.", file=sys.stderr)
        sys.exit(1)
