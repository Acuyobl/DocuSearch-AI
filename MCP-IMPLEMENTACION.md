# MCP en DocuSearch AI — revisión, prompt y guía de implementación

> **Estado: Fase 1 y Fase 2 implementadas.** Servidor stdio `DocuSearch.Mcp`, endpoint
> HTTP `/mcp`, tokens por usuario en SQL (`McpApiTokens`, hash SHA-256), expiración,
> revocación, endpoints administrativos y rate limit. Ver `DocuSearch.Mcp/README.md`,
> `tests/mcp/smoke.py` y `tests/mcp/http_smoke.py`.

## 1. Qué es MCP

**Model Context Protocol (MCP)** es un protocolo abierto, basado en JSON-RPC, que permite a un cliente de IA (Claude Desktop, OpenCode, Cursor, un agente propio) descubrir y ejecutar:

- **Tools**: funciones con parámetros y resultado estructurado. Ejemplo: `search_documents`.
- **Resources**: datos de solo lectura identificados por URI. Ejemplo: `docusearch://documents/CNT-2026-...`.
- **Prompts**: plantillas reutilizables. Ejemplo: `resume-expediente`.

MCP no es un modelo ni un orquestador. Es la **capa de contrato** entre el cliente y las capacidades autorizadas del servidor. En DocuSearch debe complementar, no sustituir, el RAG, el RBAC ni AG-02.

## 2. Revisión de procesos

| Proceso | ¿MCP aporta? | Decisión |
|---|---|---|
| Búsqueda documental autorizada | Sí, alta | **Fase 1**: tool `search_documents` |
| Lectura de una página concreta | Sí, alta | **Fase 1**: tool `get_document_page` |
| Listado de expedientes accesibles | Sí, media | **Fase 1**: tool `list_documents` |
| RAG completo con respuesta generada | Sí, con coste | **Fase 1 opcional**: `rag_query` |
| Resumen/análisis con LangGraph | Parcial | No exponer inicialmente; puede invocar coste/latencia |
| Carga de documentos | No | Solo UI/API autenticada; nunca MCP en MVP |
| Borrado/cambio de metadatos | No | Requiere aprobación dual; nunca MCP |
| Aprobaciones/rechazos | No | Solo UI auth; MCP de escritura es alto riesgo |
| Administración de usuarios/roles | No | Nunca MCP |
| Auditoría | Sí, solo lectura | Fase 2: `get_audit_summary` para auditor/admin |
| Métricas/observabilidad | Sí, solo lectura | Fase 2: `get_rag_metrics` |
| Herramientas internas de LangGraph | Riesgo alto | **No** en el MVP: el grafo es deliberadamente sin tools |

### Decisión recomendada

**Fase 1:** servidor MCP local por **stdio**, de solo lectura, con identidad fijada en configuración del servidor (no en el cliente) y RBAC reutilizado. Se registra en OpenCode como servidor MCP local.

**Fase 2:** transporte HTTP con **tokens bearer por usuario** (hash en SQL, scopes), para Claude Desktop/Cursor/otros clientes. No reutilizar cookies de la web ni permitir identidad enviada por el cliente.

**No hacer:** MCP con escritura, aprobaciones, SQL libre, shell o tool-calling dentro de LangGraph en esta etapa.

## 3. Arquitectura Fase 1

```text
Cliente MCP (OpenCode / Claude Desktop)
        │  stdio JSON-RPC
        ▼
DocuSearch.Mcp (proceso .NET separado)
  ├─ identidad única desde configuración/entorno: DOCUSEARCH_MCP_USER
  ├─ LocalSession.SignIn(claims del usuario activo)
  ├─ DocumentService.LoadAsync()  ← filtro RBAC existente
  ├─ herramientas read-only
  │    ├─ list_documents
  │    ├─ search_documents
  │    ├─ get_document_page
  │    └─ rag_query (opcional)
  ├─ auditoría de cada tool call
  └─ nunca escribe documentos
```

La identidad **no** se recibe en los argumentos. Un cliente no puede pedir “actúa como admin”. El proceso MCP tiene un usuario configurado por el operador y hereda exactamente los permisos de ese usuario.

## 4. Herramientas propuestas

### `list_documents`

```json
{ "limit": 20 }
```

Devuelve solo documentos legibles: código, título, departamento, confidencialidad, páginas y resumen. No devuelve el texto completo.

### `search_documents`

```json
{ "query": "SLA 99.99%", "limit": 8 }
```

Devuelve como máximo 8 resultados: código, título, página, fragmento truncado, score y si el usuario puede abrirlo. Debe usar el mismo retrieval híbrido y filtro que el buscador.

### `get_document_page`

```json
{ "documentCode": "CNT-2026-...", "page": 4 }
```

Devuelve el texto extraído **solo si `Permissions.CanRead`** lo permite. Valida página existente y limita el tamaño de respuesta.

### `rag_query` (opcional)

```json
{ "question": "Resume el contrato", "generate": false }
```

Reutiliza `RagQueryService`. Si `generate=true`, consume OpenCode/DeepSeek y aplica AG-02. En Fase 1 se recomienda `generate=false` para evitar coste y salida no verificada.

### Fase 2

- `get_audit_summary` (auditor/admin).
- `get_rag_metrics` (auditor/admin).
- `get_document_metadata` (sin texto).
- Recursos URI: `docusearch://documents/{code}`, `docusearch://audit/recent`.

## 5. Reglas de seguridad obligatorias

1. **Read-only**: ningún tool de escritura, borrado, aprobación, usuario o SQL.
2. **Identidad del servidor**: `Mcp:UserEmail`/`DOCUSEARCH_MCP_USER`; nunca un argumento de tool.
3. **RBAC en cada llamada**: recalcular `DocumentService.LoadAsync` o `Permissions.CanRead`; no confiar en caché de sesión.
4. **Límites**: query ≤ 2000 caracteres, `limit` 1–20, página 1–1000, respuesta ≤ 100 KB.
5. **Sin fuga de contenido**: logs solo con código de documento y metadatos, no texto, salvo `AuditContent=true`.
6. **Auditoría**: registrar `MCP_TOOL` con tool, usuario, duración y resultado; nunca el texto completo.
7. **Sin secretos**: no registrar el token/API key ni el secreto TOTP.
8. **Rate limit**: por usuario y por herramienta; `rag_query` es la más costosa.
9. **Prompt injection**: el texto de documentos es dato, no instrucción; no ejecutar órdenes contenidas en páginas.
10. **Errores seguros**: devolver “no autorizado” sin revelar la existencia de un documento clasificado.

## 6. Estructura de archivos propuesta

```text
DocuSearch.sln
├─ DocuSearch.csproj                     # app existente
├─ DocuSearch.Mcp/
│  ├─ DocuSearch.Mcp.csproj              # net10.0, ModelContextProtocol 2.2.0
│  ├─ Program.cs                         # stdio server + DI
│  ├─ McpIdentity.cs                     # carga usuario activo y claims
│  ├─ McpTools.cs                        # [McpServerToolType]
│  ├─ McpOptions.cs                      # UserEmail, MaxResults, etc.
│  └─ README.md
├─ Services/Mcp/                         # (Fase 2 HTTP)
│  ├─ McpApiToken.cs
│  ├─ McpTokenService.cs
│  └─ McpHttpTools.cs
└─ Data/Migrations/...                   # tabla McpApiTokens en Fase 2
```

## 7. OpenCode: registro del servidor MCP

En `~/.config/opencode/opencode.json`:

```json
{
  "mcp": {
    "docusearch": {
      "type": "local",
      "command": [
        "C:/Programación/DocuSearch-AI/DocuSearch.Mcp/bin/Release/net10.0/DocuSearch.Mcp.exe"
      ],
      "environment": {
        "DOCUSEARCH_MCP_USER": "usuario@example.test"
      },
      "enabled": true
    }
  }
}
```

En desarrollo puede usarse:

```json
"command": ["dotnet", "run", "--project", "DocuSearch.Mcp/DocuSearch.Mcp.csproj", "--no-build"]
```

No guardar contraseñas ni secretos TOTP en la configuración MCP.

---

# PROMPT PARA IMPLEMENTAR

Copia este bloque completo en el agente de implementación.

---

Actúa como ingeniero senior .NET en **DocuSearch AI** (Blazor Server, .NET 10, EF Core, SQL Server, RAG con Pinecone y OpenCode/DeepSeek).

## Tarea

Implementa la **Fase 1 de MCP**: un servidor MCP local por stdio, de solo lectura, que exponga capacidades documentales respetando el mismo RBAC de la aplicación. No implementes escritura ni administración.

## Contexto obligatorio

Lee antes de tocar código:

- `Services/Permissions.cs`, `Services/DocumentService.cs`.
- `Services/Rag/RagQueryService.cs`, `Services/Rag/HybridRagRetriever.cs`.
- `Services/AgentVerifier.cs`.
- `Program.cs`, `Data/AppDbContext.cs`, `Models/Domain.cs`.
- `RBAC-ACCESO.md`, `MCP-IMPLEMENTACION.md`.
- `~/.config/opencode/opencode.json` solo para el ejemplo de registro; no lo modifiques sin pedir permiso.
- Paquete NuGet cacheado: `ModelContextProtocol` 2.2.0.

## Objetivo funcional

Crear `DocuSearch.Mcp` con transporte stdio y estas tools:

1. `list_documents(limit)`.
2. `search_documents(query, limit)`.
3. `get_document_page(documentCode, page)`.
4. `rag_query(question, generate=false)`.

Reglas:

- La identidad se resuelve **solo** desde `Mcp:UserEmail` o `DOCUSEARCH_MCP_USER`. No aceptes `userId`, `role` ni `email` como parámetros de tool.
- Carga el usuario desde SQL; exige `Status="Activo"`; crea `LocalSession`/claims con su rol y departamento.
- Cada tool debe llamar a `DocumentService.LoadAsync()` y comprobar `Permissions.CanRead` antes de devolver datos.
- Si el documento no es legible, responde con un error genérico `not_authorized` sin revelar si existe.
- `get_document_page` valida página existente y limita el texto a 20 000 caracteres.
- `search_documents` devuelve como máximo 20 resultados y fragmentos truncados a 500 caracteres.
- `rag_query` con `generate=true` debe reutilizar `RagQueryService` y devolver sus citas; aplica AG-02.
- No implementes tools de escritura, aprobación, borrado, SQL, shell ni administración.
- Audita cada llamada como `MCP_TOOL`, sin texto de documentos en claro.

## Requisitos técnicos

- Crear `DocuSearch.Mcp/DocuSearch.Mcp.csproj` (`net10.0`, `ModelContextProtocol` 2.2.0) con `ProjectReference` a `DocuSearch.csproj`.
- `Program.cs` configura DI, logging, `AppDbContext`, servicios existentes y `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`.
- No arrancar Blazor ni abrir puertos.
- No duplicar lógica de permisos; reutilizar `Permissions` y `DocumentService`.
- No usar `MarkupString` ni HTML.
- Validar todas las entradas con DataAnnotations o validación explícita.
- Manejar cancelación y timeouts.
- No registrar secretos, tokens ni contenido clasificado.

## Pruebas obligatorias

Añadir un bloque `--mcp` a `tests/Program.cs` o un proyecto de pruebas específico:

- [ ] El usuario `consultor` no puede listar ni leer documentos Confidencial/Secreto.
- [ ] `get_document_page` de un documento no autorizado devuelve error genérico.
- [ ] `search_documents` no devuelve códigos no autorizados.
- [ ] `rag_query` no recibe fuentes no autorizadas.
- [ ] Parámetros inválidos (`limit=0`, `page=0`, query vacía) devuelven error de validación.
- [ ] La tool de lectura no escribe en la base.
- [ ] El servidor inicializa MCP y enumera las tools.
- [ ] No se abre red ni se arranca Blazor.
- [ ] Las suites actuales siguen pasando.

## Criterios de aceptación

1. `dotnet build DocuSearch.sln` compila.
2. Un cliente MCP local enumera las tools.
3. Todas las tools respetan el rol del usuario configurado.
4. No existe ninguna tool de escritura.
5. No hay fugas de documentos no autorizados en texto, metadatos ni errores.
6. Cada llamada queda auditada.
7. `--mcp` pasa con 0 fallos y las suites existentes siguen pasando.
8. Documentar en `DocuSearch.Mcp/README.md` cómo registrar el servidor en OpenCode y cómo cambiar de usuario de forma segura.

## Entrega

- Archivos creados/modificados.
- Comandos de build/tests y resultados.
- Ejemplo de configuración de OpenCode.
- Limitaciones y siguientes pasos (HTTP + bearer en Fase 2).
- Commit con mensaje `feat(mcp): servidor MCP read-only con RBAC`.

No implementes la Fase 2 ni cambios de escritura sin autorización expresa.

---

## 8. Plan de pruebas resumido

| Nivel | Caso |
|---|---|
| Protocolo | initialize, list_tools, call_tool con JSON válido |
| RBAC | consultor vs admin vs auditor vs admin_depto |
| Datos | listar, buscar, leer página, página inexistente |
| RAG | `rag_query` sin generación y con generación |
| Seguridad | identidad no parametrizable, error genérico, sin escritura |
| Auditoría | evento `MCP_TOOL` por llamada |
| Integración | OpenCode descubre y ejecuta `docusearch` |

## 9. Fases

- **Fase 1 (recomendada):** stdio local, read-only, un usuario configurado.
- **Fase 2:** HTTP + bearer tokens por usuario, scopes, rate limit, Claude Desktop/Cursor.
- **Fase 3:** recursos/prompts MCP y métricas/auditoría read-only.- **Nunca:** escritura, aprobaciones, administración o SQL libre por MCP.
