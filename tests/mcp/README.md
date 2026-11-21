# Pruebas del servidor MCP

## Smoke HTTP

Con la app arrancada y `Mcp:Http` activo:

```powershell
$env:MCP_HTTP_URL = "http://127.0.0.1:5068/mcp"
$env:MCP_HTTP_TOKEN = "<token>"
python tests/mcp/http_smoke.py
```

Comprueba initialize, tools/list, tools/call y rechazo de token incorrecto. Sin token
el endpoint debe responder **401**.

## Smoke del protocolo stdio

```powershell
$env:DOCUSEARCH_MCP_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch;User Id=sa;Password=<secreto>;TrustServerCertificate=True;Encrypt=False"
$env:DOCUSEARCH_MCP_USER = "usuario@example.test"
python tests/mcp/smoke.py
```

Comprueba:

- `initialize`,
- `tools/list` con las 4 tools,
- `list_documents`,
- `search_documents`,
- `get_document_page`,
- `rag_query`.

## RBAC con usuario de bajo privilegio

Crear un usuario `consultor` activo en una base de prueba y ejecutar:

```powershell
$env:DOCUSEARCH_MCP_USER = "mcp.consultor@example.test"
$env:MCP_EXPECT_RESTRICTED = "1"
$env:MCP_PAGE_CODE = "REG-2026-55F63E9C1E3F"
python tests/mcp/smoke.py
```

Resultado esperado:

- el listado no contiene documentos `Confidencial` ni `Estrictamente Secreto`;
- `get_document_page` responde `not_authorized`;
- las suites no fallan.

No guardar usuarios ni secretos de prueba en el repositorio.

## Arnés pytest

Instala y ejecuta:

```powershell
python -m pip install -r tests/mcp/requirements.txt

Set-Location "D:\Programación\DocuSearch-AI"
$env:DOCUSEARCH_MCP_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch;User Id=sa;Password=<secreto>;TrustServerCertificate=True;Encrypt=False"
$env:DOCUSEARCH_MCP_USER = "acuyobl@gmail.com"
$env:MCP_LOW_USER = "consultor.demo@example.test"
$env:MCP_RESTRICTED_DOCUMENT_CODE = "REG-2026-55F63E9C1E3F"

python -m pytest tests/mcp -q
```

Para HTTP y ciclo de tokens:

```powershell
$env:MCP_HTTP_URL = "http://127.0.0.1:5068/mcp"
$env:MCP_HTTP_TOKEN = "<token>"
$env:MCP_ADMIN_EMAIL = "acuyobl@gmail.com"
$env:MCP_ADMIN_TOTP_SECRET = "<secreto-totp>"
python -m pytest tests/mcp -q
```

- Sin `MCP_HTTP_TOKEN`, las pruebas HTTP se omiten.
- Sin `MCP_ADMIN_*`, el ciclo de revocación y auditoría se omiten.
- Fallos de snippet/página revelan regresiones reales del contrato.
