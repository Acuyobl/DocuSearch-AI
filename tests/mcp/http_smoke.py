"""Smoke test del endpoint MCP HTTP.

Variables obligatorias:
  MCP_HTTP_URL    URL completa, por ejemplo http://127.0.0.1:5068/mcp
  MCP_HTTP_TOKEN  Token bearer configurado en Mcp:Http:Token
"""
import json
import os
import urllib.error
import urllib.request

url = os.environ.get("MCP_HTTP_URL", "http://127.0.0.1:5068/mcp")
token = os.environ.get("MCP_HTTP_TOKEN")
if not token:
    raise SystemExit("Falta MCP_HTTP_TOKEN.")


def post(payload):
    request = urllib.request.Request(
        url,
        data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
        method="POST",
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        body = response.read().decode("utf-8")
        data = [line[6:] for line in body.splitlines() if line.startswith("data: ")]
        return response.status, json.loads(data[-1]) if data else {}


status, init = post({
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "docs-http-smoke", "version": "1.0"},
    },
})
assert status == 200 and "result" in init, init
print("OK initialize")

post({"jsonrpc": "2.0", "method": "notifications/initialized"})

status, tools = post({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
names = {tool["name"] for tool in tools.get("result", {}).get("tools", [])}
expected = {"list_documents", "search_documents", "get_document_page", "rag_query"}
assert status == 200 and expected <= names, names
print("OK tools:", sorted(expected))

status, call = post({
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {"name": "list_documents", "arguments": {"limit": 2}},
})
assert status == 200 and call.get("result", {}).get("content"), call
print("OK tools/call list_documents")

try:
    bad = urllib.request.Request(
        url,
        data=b"{}",
        method="POST",
        headers={
            "Authorization": "Bearer incorrecto",
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        },
    )
    urllib.request.urlopen(bad, timeout=10)
    raise AssertionError("El token incorrecto no fue rechazado")
except urllib.error.HTTPError as error:
    assert error.code == 401, error.code
    print("OK token incorrecto rechazado")
