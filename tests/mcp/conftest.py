"""Fixtures del arnés MCP.

No contiene credenciales. Todo se toma de variables de entorno y se omite (skip)
cuando falta el servicio o la identidad de prueba.
"""
from __future__ import annotations

import os
from pathlib import Path

import pytest

from client import McpHttpClient, McpStdioClient, AdminApi

ROOT = Path(__file__).resolve().parents[2]


def _connection() -> str | None:
    return os.environ.get("DOCUSEARCH_MCP_CONNECTION") or os.environ.get("ConnectionStrings__Documents")


def _stdio_user() -> str | None:
    return os.environ.get("DOCUSEARCH_MCP_USER") or os.environ.get("MCP_TEST_USER")


@pytest.fixture(scope="session")
def project_root() -> Path:
    return ROOT


@pytest.fixture(scope="session")
def stdio_dll(project_root: Path) -> str:
    path = project_root / "DocuSearch.Mcp" / "bin" / "Debug" / "net10.0" / "DocuSearch.Mcp.dll"
    if not path.exists():
        pytest.skip("Compila DocuSearch.Mcp antes de ejecutar: dotnet build DocuSearch.Mcp")
    return str(path)


@pytest.fixture
def stdio_factory(project_root: Path, stdio_dll: str):
    connection = _connection()
    user = _stdio_user()
    if not connection or not user:
        pytest.skip("Faltan DOCUSEARCH_MCP_CONNECTION y DOCUSEARCH_MCP_USER.")

    clients: list[McpStdioClient] = []

    def make(user_override: str | None = None) -> McpStdioClient:
        client = McpStdioClient(connection, user_override or user, stdio_dll, str(project_root))
        client.initialize()
        clients.append(client)
        return client

    yield make
    for client in clients:
        client.close()


@pytest.fixture
def stdio(stdio_factory):
    return stdio_factory()


@pytest.fixture
def sample_document(stdio_factory):
    client = stdio_factory()
    documents = client.call("list_documents", {"limit": 1})
    if not documents:
        pytest.skip("No hay documentos legibles en el entorno de prueba.")
    return documents[0]


@pytest.fixture(scope="session")
def http_url() -> str:
    return os.environ.get("MCP_HTTP_URL", "http://127.0.0.1:5068/mcp")


@pytest.fixture(scope="session")
def http_token() -> str:
    token = os.environ.get("MCP_HTTP_TOKEN")
    if not token:
        pytest.skip("Falta MCP_HTTP_TOKEN.")
    return token


@pytest.fixture
def http_client(http_url: str, http_token: str):
    client = McpHttpClient(http_url, http_token)
    client.initialize()
    return client


@pytest.fixture
def admin_api() -> AdminApi:
    email = os.environ.get("MCP_ADMIN_EMAIL")
    secret = os.environ.get("MCP_ADMIN_TOTP_SECRET")
    if not email or not secret:
        pytest.skip("Faltan MCP_ADMIN_EMAIL y MCP_ADMIN_TOTP_SECRET.")
    api = AdminApi(os.environ.get("MCP_APP_URL", "http://127.0.0.1:5068"), email, secret)
    api.login()
    return api
