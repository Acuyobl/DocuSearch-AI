"""Smoke test del servidor MCP por stdio.

Variables obligatorias:
  DOCUSEARCH_MCP_CONNECTION  Cadena SQL del entorno de pruebas.
  DOCUSEARCH_MCP_USER        Correo del usuario MCP autorizado.

Opcionales:
  MCP_PAGE_CODE             Documento a leer (por defecto uno público).
  MCP_EXPECT_RESTRICTED=1   Exige que el usuario no vea documentos Confidencial/Secreto.
"""
import json
import os
import subprocess
import sys

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
dll = os.path.join(root, "DocuSearch.Mcp", "bin", "Debug", "net10.0", "DocuSearch.Mcp.dll")

connection = os.environ.get("DOCUSEARCH_MCP_CONNECTION") or os.environ.get("ConnectionStrings__Documents")
user = os.environ.get("DOCUSEARCH_MCP_USER")
if not connection:
    raise SystemExit("Falta DOCUSEARCH_MCP_CONNECTION.")
if not user:
    raise SystemExit("Falta DOCUSEARCH_MCP_USER.")

env = os.environ.copy()
env["DOCUSEARCH_MCP_CONNECTION"] = connection
env["DOCUSEARCH_MCP_USER"] = user

proc = subprocess.Popen(
    ["dotnet", dll],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL,
    text=True,
    encoding="utf-8",
    bufsize=1,
    env=env,
)


def send(message):
    proc.stdin.write(json.dumps(message, ensure_ascii=False) + "\n")
    proc.stdin.flush()


def receive():
    line = proc.stdout.readline()
    if not line:
        raise RuntimeError("MCP cerró stdout")
    return json.loads(line)


def tool_call(request_id, name, arguments):
    send({"jsonrpc": "2.0", "id": request_id, "method": "tools/call",
          "params": {"name": name, "arguments": arguments}})
    response = receive()
    content = response.get("result", {}).get("content", [])
    if not content:
        return response
    return json.loads(content[0].get("text", "null"))


try:
    send({
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "docusearch-smoke", "version": "1.0"},
        },
    })
    init = receive()
    assert "result" in init, init
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
    tools = receive()
    names = {tool.get("name") for tool in tools.get("result", {}).get("tools", [])}
    expected = {"list_documents", "search_documents", "get_document_page", "rag_query"}
    assert expected <= names, names
    print("OK tools:", sorted(expected))

    documents = tool_call(3, "list_documents", {"limit": 20})
    assert isinstance(documents, list) and documents, documents
    if os.environ.get("MCP_EXPECT_RESTRICTED") == "1":
        assert all(document["confidentiality"] not in ("Confidencial", "Estrictamente Secreto")
                   for document in documents), documents
        print("OK rol bajo: sin documentos Confidencial/Secreto en el listado")

    hits = tool_call(4, "search_documents", {"query": "Mesopotamia", "limit": 3})
    assert isinstance(hits, list), hits
    print("OK search_documents:", len(hits), "resultado(s)")

    page = tool_call(5, "get_document_page", {
        "documentCode": os.environ.get("MCP_PAGE_CODE", "MAN-2026-79618166FCB9"),
        "page": 1,
    })
    if os.environ.get("MCP_EXPECT_RESTRICTED") == "1":
        assert page.get("error") == "not_authorized", page
        print("OK rol bajo: página restringida denegada")
    else:
        assert "text" in page and page["text"], page
        print("OK get_document_page:", page.get("internalCode"))

    answer = tool_call(6, "rag_query", {"question": "¿Cuántos documentos hay?"})
    assert "text" in answer, answer
    print("OK rag_query:", answer["text"].splitlines()[0][:80])
finally:
    proc.stdin.close()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
