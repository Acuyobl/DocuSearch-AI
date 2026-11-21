"""Contrato del protocolo MCP y del servidor stdio."""
from __future__ import annotations


EXPECTED_TOOLS = {"get_document_page", "list_documents", "rag_query", "search_documents"}


def test_initialize_and_tools(stdio):
    tools = stdio.list_tools()
    names = {tool["name"] for tool in tools}
    assert names == EXPECTED_TOOLS
    for tool in tools:
        assert tool.get("description")
        assert tool.get("inputSchema", {}).get("type") == "object"


def test_no_write_tools(stdio):
    forbidden = {"delete_document", "upload_document", "approve", "reject", "admin", "sql"}
    names = {tool["name"] for tool in stdio.list_tools()}
    assert not names & forbidden


def test_unknown_method_is_controlled(stdio):
    # Un método JSON-RPC inexistente no debe tumbar el proceso.
    response = stdio._send({"jsonrpc": "2.0", "id": 999, "method": "tools/nope", "params": {}})
    assert response.get("error") or "result" in response
    assert stdio.list_tools()
