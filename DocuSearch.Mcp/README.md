# Servidor MCP de DocuSearch

Dos transportes con el mismo contrato read-only:

- **stdio**: proceso separado `DocuSearch.Mcp` para OpenCode/Claude Desktop local.
- **HTTP**: endpoint `/mcp` de la app, autenticado con bearer y tokens por usuario en SQL.

## Requisitos

- .NET 10.
- SQL Server con la base `DocuSearch` y las migraciones aplicadas (incluye `McpApiTokens`).
- stdio: `DOCUSEARCH_MCP_USER` y `DOCUSEARCH_MCP_CONNECTION`.
- HTTP: `Mcp__Http__Enabled=true` y un token creado por la API administrativa.
- Permiso de lectura en SQL para el usuario.

No se guardan secretos en el repositorio. `appsettings.json` deja el HTTP desactivado.

## HTTP en la app

```powershell
$env:Mcp__Http__Enabled = "true"
```

El endpoint queda en `http://127.0.0.1:5068/mcp`.

### Crear un token (administrador, cookie de sesión)

```powershell
curl.exe -s -b cookies.txt -H "Content-Type: application/json" `
  -X POST http://127.0.0.1:5068/api/mcp/tokens `
  --data '{"name":"OpenCode","validDays":90}'
```

La respuesta incluye `token` **una sola vez**. Se guarda solo su SHA-256.

### Listar y revocar

```powershell
curl.exe -s -b cookies.txt http://127.0.0.1:5068/api/mcp/tokens
curl.exe -s -b cookies.txt -X DELETE http://127.0.0.1:5068/api/mcp/tokens/<id>
```

- Sin token, token incorrecto, revocado o vencido: **401**.
- Con token válido: JSON-RPC MCP y RBAC real del usuario.
- Rate limit: `Mcp__RateLimitPerMinute` (60 por defecto), por usuario/IP.

`Mcp__Http__User`/`Mcp__Http__Token` quedan solo como compatibilidad de arranque local.
Los tokens nuevos deben crearse por la API y guardarse como un secreto.

## Compilar

```powershell
dotnet build DocuSearch.Mcp/DocuSearch.Mcp.csproj -c Release
```

## Registrar en OpenCode

### stdio

```json
{
  "mcp": {
    "docusearch": {
      "type": "local",
      "command": [
        "C:/Programación/DocuSearch-AI/DocuSearch.Mcp/bin/Release/net10.0/DocuSearch.Mcp.exe"
      ],
      "enabled": true
    }
  }
}
```

Arranca OpenCode desde una consola que tenga definidas `DOCUSEARCH_MCP_USER` y
`DOCUSEARCH_MCP_CONNECTION`.

### HTTP

Usa `DocuSearch.Mcp/opencode.remote.sample.json` y sustituye el token. La URL es
`http://127.0.0.1:5068/mcp`.

## Tools

- `list_documents(limit)`: metadatos de expedientes legibles.
- `search_documents(query, limit)`: búsqueda léxica sobre documentos legibles.
- `get_document_page(documentCode, page)`: texto de una página autorizada.
- `rag_query(question)`: respuesta extractiva con citas; no llama a un LLM.

Todas las tools:

- resuelven identidad desde el servidor o el token; nunca desde argumentos;
- aplican `Permissions`/`DocumentService`;
- registran `MCP_TOOL` en auditoría;
- no escriben documentos ni exponen tools de administración.

## Pruebas

```powershell
# stdio
$env:DOCUSEARCH_MCP_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch;User Id=sa;Password=<secreto>;TrustServerCertificate=True;Encrypt=False"
$env:DOCUSEARCH_MCP_USER = "usuario@example.test"
python tests/mcp/smoke.py

# HTTP con token creado por la API
$env:MCP_HTTP_URL = "http://127.0.0.1:5068/mcp"
$env:MCP_HTTP_TOKEN = "<token>"
python tests/mcp/http_smoke.py
```

Prueba de rol bajo:

```powershell
$env:MCP_EXPECT_RESTRICTED = "1"
$env:MCP_PAGE_CODE = "REG-2026-55F63E9C1E3F"
python tests/mcp/smoke.py
```

## Limitaciones

- La API de tokens no tiene UI; se gestiona por endpoints administrativos.
- `rag_query` es extractivo; no usa OpenCode/DeepSeek ni LangGraph.
- No hay tools de escritura, aprobación, administración ni SQL libre.
- El transporte stdio no abre puertos.
