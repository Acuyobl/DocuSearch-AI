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
