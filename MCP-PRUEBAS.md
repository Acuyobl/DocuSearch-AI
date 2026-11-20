# Cómo probar MCP en DocuSearch

Hay dos formas: **stdio** (proceso local, ideal para OpenCode) y **HTTP** (endpoint `/mcp`, ideal para clientes remotos).

## Requisitos generales

- App/DLL compilados: `dotnet build DocuSearch.sln -c Debug`.
- Base `DocuSearch` migrada y un usuario activo.
- Cadena SQL en `DOCUSEARCH_MCP_CONNECTION` (no la guardes en el repositorio).
- No se necesita Python ni OpenCode para probar las tools: `rag_query` es extractivo.

---

## Opción A — stdio (recomendada para la primera prueba)

### 1. Compilar

```powershell
dotnet build DocuSearch.Mcp/DocuSearch.Mcp.csproj -c Debug
```

### 2. Definir identidad y conexión

```powershell
$env:DOCUSEARCH_MCP_CONNECTION = "Server=127.0.0.1,14331;Database=DocuSearch;User Id=sa;Password=<secreto>;TrustServerCertificate=True;Encrypt=False"
$env:DOCUSEARCH_MCP_USER = "acuyobl@gmail.com"
```

`DOCUSEARCH_MCP_USER` debe corresponder a un usuario **activo** de la tabla `Users`. El rol real de ese usuario se aplica automáticamente.

### 3. Ejecutar la prueba

```powershell
python tests/mcp/smoke.py
```

Salida esperada:

```text
OK tools: ['get_document_page', 'list_documents', 'rag_query', 'search_documents']
OK search_documents: 3 resultado(s)
OK get_document_page: MAN-2026-79618166FCB9
OK rag_query: 12 expediente(s) accesible(s).
```

### 4. Probar RBAC con rol bajo

Usa un usuario `consultor` activo y comprueba que no ve el documento Confidencial:

```powershell
$env:DOCUSEARCH_MCP_USER = "consultor.demo@example.test"
$env:MCP_EXPECT_RESTRICTED = "1"
$env:MCP_PAGE_CODE = "REG-2026-55F63E9C1E3F"
python tests/mcp/smoke.py
```

Esperado: sin documentos `Confidencial`/`Estrictamente Secreto` y `not_authorized` al leer la página restringida.

### 5. Usarlo desde OpenCode

En `~/.config/opencode/opencode.json`:

```json
{
  "mcp": {
    "docusearch": {
      "type": "local",
      "command": [
        "C:/Programación/DocuSearch-AI/DocuSearch.Mcp/bin/Debug/net10.0/DocuSearch.Mcp.exe"
      ],
      "enabled": true
    }
  }
}
```

Arranca OpenCode desde una consola que tenga definidas `DOCUSEARCH_MCP_USER` y `DOCUSEARCH_MCP_CONNECTION`.

---

## Opción B — HTTP `/mcp`

### 1. Activar el endpoint

La configuración por defecto es segura: `Mcp:Http:Enabled=false`.

En local:

```powershell
$env:Mcp__Http__Enabled = "true"
dotnet run --project DocuSearch.csproj
```

En Docker, añade la variable al arranque del contenedor y reinícialo:

```powershell
# ejemplo dentro de obj/_docker-run.ps1
-e 'Mcp__Http__Enabled=true'
docker restart ds-app
```

### 2. Crear un token

El endpoint administrativo usa la **cookie de sesión** de un administrador.

1. Inicia sesión en `http://127.0.0.1:5068` con un usuario `admin_general`.
2. Abre DevTools → Console en esa misma pestaña y ejecuta:

```js
fetch('/api/mcp/tokens', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ name: 'Prueba MCP', validDays: 7 })
}).then(r => r.json()).then(console.log)
```

3. Copia el campo `token` de la respuesta. **Solo se muestra una vez.**

También puedes listar y revocar:

```js
fetch('/api/mcp/tokens').then(r => r.json()).then(console.log)
fetch('/api/mcp/tokens/<id>', { method: 'DELETE' }).then(r => console.log(r.status))
```

### 3. Probar el endpoint

```powershell
$env:MCP_HTTP_URL = "http://127.0.0.1:5068/mcp"
$env:MCP_HTTP_TOKEN = "dsmcp_..."
python tests/mcp/http_smoke.py
```

Esperado:

```text
OK initialize
OK tools: ['get_document_page', 'list_documents', 'rag_query', 'search_documents']
OK tools/call list_documents
OK token incorrecto rechazado
```

Sin token o con token incorrecto/revocado, el endpoint responde **401**.

### 4. Usarlo desde OpenCode

Usa `DocuSearch.Mcp/opencode.remote.sample.json`, sustituye el token y arranca OpenCode:

```json
{
  "mcp": {
    "docusearch": {
      "type": "remote",
      "url": "http://127.0.0.1:5068/mcp",
      "headers": { "Authorization": "Bearer dsmcp_..." },
      "enabled": true
    }
  }
}
```

---

## Prueba manual JSON-RPC (sin scripts)

Con el endpoint HTTP activo y un token válido:

```powershell
$headers = @{
  Authorization = "Bearer dsmcp_..."
  Accept = "application/json, text/event-stream"
}
$body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"manual","version":"1.0"}}}'
Invoke-WebRequest -Uri 'http://127.0.0.1:5068/mcp' -Method POST -Headers $headers `
  -ContentType 'application/json' -Body $body -UseBasicParsing
```

La respuesta es `text/event-stream`; la línea `data:` contiene el JSON.

---

## Problemas frecuentes

| Síntoma | Causa probable |
|---|---|
| `401` siempre | Endpoint HTTP desactivado, token revocado/vencido o encabezado incorrecto |
| `Falta DOCUSEARCH_MCP_USER` | Variable de entorno no definida en la consola de OpenCode |
| No enumera tools | Ruta del DLL incorrecta o proyecto MCP no compilado |
| No conecta a SQL | Cadena incorrecta; en Docker local usa el puerto proxy `14331` |
| Usuario sin documentos | Usuario inactivo, rol sin acceso o departamento distinto |
| `rate_limited` | Límite de login; espera un minuto |
| TOTP rechazado | Código de 30 s reutilizado; espera al siguiente intervalo |

## Comandos de verificación rápida

```powershell
dotnet build DocuSearch.sln -c Debug
python tests/mcp/smoke.py          # stdio
python tests/mcp/http_smoke.py     # HTTP
```
