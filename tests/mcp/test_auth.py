"""Autenticación HTTP y ciclo de vida de tokens."""
from __future__ import annotations

import pytest

from client import McpError, McpHttpClient


def test_missing_token_is_rejected(http_url):
    client = McpHttpClient(http_url, None)
    with pytest.raises(McpError):
        client.initialize()


def test_wrong_token_is_rejected(http_url):
    client = McpHttpClient(http_url, "token-invalido")
    with pytest.raises(McpError):
        client.initialize()


def test_valid_token_lists_tools(http_client):
    names = {tool["name"] for tool in http_client.list_tools()}
    assert {"list_documents", "search_documents", "get_document_page", "rag_query"} <= names


def test_token_revocation(http_url, admin_api):
    created = admin_api.create_token("pytest-revocacion", valid_days=1)
    token = created["token"]
    token_id = created["access"]["id"]
    client = McpHttpClient(http_url, token)
    client.initialize()
    assert client.list_tools()
    admin_api.revoke_token(token_id)
    with pytest.raises(McpError):
        McpHttpClient(http_url, token).initialize()
