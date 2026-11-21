"""RBAC del servidor MCP con un usuario de bajo privilegio."""
from __future__ import annotations

import os

import pytest


def test_consultor_never_sees_restricted_documents(stdio_factory):
    low_user = os.environ.get("MCP_LOW_USER")
    if not low_user:
        pytest.skip("Falta MCP_LOW_USER.")
    client = stdio_factory(low_user)
    documents = client.call("list_documents", {"limit": 50})
    assert documents
    assert all(doc["confidentiality"] not in ("Confidencial", "Estrictamente Secreto")
               for doc in documents)


def test_consultor_cannot_read_restricted_page(stdio_factory):
    low_user = os.environ.get("MCP_LOW_USER")
    code = os.environ.get("MCP_RESTRICTED_DOCUMENT_CODE")
    if not low_user or not code:
        pytest.skip("Faltan MCP_LOW_USER y MCP_RESTRICTED_DOCUMENT_CODE.")
    client = stdio_factory(low_user)
    result = client.call("get_document_page", {"documentCode": code, "page": 1})
    assert result.get("error") == "not_authorized"
    assert "text" not in result
