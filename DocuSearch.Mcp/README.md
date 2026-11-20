# Servidor MCP local de DocuSearch

Servidor **stdio** de solo lectura para OpenCode, Claude Desktop u otros clientes MCP.

## Requisitos

- .NET 10.
- SQL Server con la base `DocuSearch` y el esquema migrado.
- Variables de entorno:
  - `DOCUSEARCH_MCP_USER`: correo de un usuario activo. El servidor usa su rol real.
  - `DOCUSEARCH_MCP_CONNECTION`: cadena de conexión SQL.
    También se acepta `ConnectionStrings__Documents`.
- Permiso de lectura en SQL para ese usuario.

No se guardan credenciales en el repositorio.

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
