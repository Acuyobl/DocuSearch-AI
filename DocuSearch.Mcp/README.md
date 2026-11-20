# Servidor MCP de DocuSearch

Dos transportes con el mismo contrato read-only:

- **stdio**: proceso separado `DocuSearch.Mcp` para OpenCode/Claude Desktop local.
- **HTTP**: endpoint `/mcp` de la app, autenticado con bearer. Ideal para clientes remotos.

## Requisitos

- .NET 10.
- SQL Server con la base `DocuSearch` y el esquema migrado.
- Variables de entorno:
  - stdio: `DOCUSEARCH_MCP_USER` y `DOCUSEARCH_MCP_CONNECTION`.
  - HTTP: `Mcp__Http__Enabled=true`, `Mcp__Http__User` y `Mcp__Http__Token`.
- Permiso de lectura en SQL para ese usuario.

No se guardan credenciales en el repositorio. En `appsettings.json` la sección HTTP
queda desactivada y vacía por defecto.

## HTTP en la app

```powershell
$env:Mcp__Http__Enabled = "true"
$env:Mcp__Http__User = "usuario@example.test"
$env:Mcp__Http__Token = "<token-largo-y-aleatorio>"
```

El endpoint queda en `http://127.0.0.1:5068/mcp`.

- Sin `Authorization: Bearer ...` responde **401**.
- Con token incorrecto responde **401**.
- Con token correcto acepta JSON-RPC MCP y resuelve el rol real del usuario en SQL.

> Esta es la Fase 2 MVP con un token de configuración por usuario. Los tokens multiusuario
> hasheados en SQL y su UI de administración quedan como siguiente fase.

## Compilar

```powershell
dotnet build DocuSearch.Mcp/DocuSearch.Mcp.csproj -c Release
```

## Registrar en OpenCode

En `~/.config/opencode/opencode.json`:

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
`DOCUSEARCH_MCP_CONNECTION`. Así las credenciales no quedan en el archivo de configuración.

## Registrar cliente remoto en OpenCode

Con `Mcp:Http` activo, usa `DocuSearch.Mcp/opencode.remote.sample.json` y sustituye el
token. La URL es `http://127.0.0.1:5068/mcp`.

## Tools

- `list_documents(limit)`: metadatos de expedientes legibles.
- `search_documents(query, limit)`: búsqueda léxica sobre documentos legibles.
- `get_document_page(documentCode, page)`: texto de una página autorizada.
- `rag_query(question)`: respuesta extractiva con citas; no llama a un LLM.

Todas las tools:
- resuelven identidad solo desde el entorno del servidor,
- aplican `Permissions`/`DocumentService`,
- registran `MCP_TOOL` en auditoría,
- no escriben documentos ni exponen tools de administración.

## Prueba de humo

Con una base de prueba:

```powershell
$env:DOCUSEARCH_MCP_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch;User Id=sa;Password=<secreto>;TrustServerCertificate=True;Encrypt=False"
$env:DOCUSEARCH_MCP_USER = "usuario@example.test"
python tests/mcp/smoke.py
```

Prueba de rol bajo:

```powershell
$env:MCP_EXPECT_RESTRICTED = "1"
$env:MCP_PAGE_CODE = "REG-2026-55F63E9C1E3F"
python tests/mcp/smoke.py
```

El segundo caso exige que el usuario no vea documentos `Confidencial`/`Estrictamente Secreto`
ni pueda leer la página restringida.

## Limitaciones

- Un servidor = un usuario. Para multiusuario se necesita Fase 2 con HTTP y bearer tokens.
- `rag_query` es extractivo; no usa OpenCode/DeepSeek ni LangGraph.
- No hay tools de escritura, aprobación, administración ni SQL libre.
- El transporte es stdio; no abre puertos.
