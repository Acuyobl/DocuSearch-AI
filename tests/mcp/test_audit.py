"""Auditoría de llamadas MCP (requiere credenciales de administrador de prueba)."""
from __future__ import annotations

import pytest


def test_mcp_tool_is_audited(stdio_factory, admin_api):
    client = stdio_factory()
    client.call("list_documents", {"limit": 1})
    assert "MCP_TOOL" in admin_api.audit_html()
