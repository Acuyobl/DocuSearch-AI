# MCP aplicado a las pruebas de DocuSearch AI

## 1. Decisión de arquitectura

**Sí conviene usar MCP como capa de acceso para pruebas de integración y seguridad de solo lectura.**

MCP no sustituye a:

- las pruebas unitarias C# (`tests/Program.cs`);
- las pruebas de workers Python;
- Playwright para la UI;
- la evaluación RAG (`--rag-eval` / DeepEval).

MCP sí aporta:

- un contrato estable para verificar búsqueda, lectura y RBAC;
- pruebas independientes de la UI;
- validación del servidor y del cliente MCP;
- una forma de comprobar que un agente externo solo ve lo autorizado.

### Regla de oro

> MCP se usa para **leer y verificar**. Los datos de prueba se crean por la UI, la API existente o un fixture SQL de la base `_tests`. No se convierte MCP en una API de escritura para tests.

## 2. Estado actual reutilizable

| Pieza | Archivo | Uso en pruebas |
|---|---|---|
| Servidor stdio | `DocuSearch.Mcp/` | Pruebas rápidas sin HTTP ni token |
| Endpoint HTTP | `/mcp` en la app | Pruebas de autenticación y cliente externo |
| Tools | `Services/Mcp/ReadOnlyDocumentTools.cs` | Contrato funcional |
| Token SQL | `McpApiTokens` | Auth, expiración y revocación |
| Auth | `Services/Mcp/McpHttpAuth.cs` | Bearer y RBAC |
| Smoke stdio | `tests/mcp/smoke.py` | Base del arnés |
| Smoke HTTP | `tests/mcp/http_smoke.py` | Base del arnés HTTP |
| E2E UI | `tests/e2e/` | Complemento, no sustituto |
| CI | `.github/workflows/ci.yml` | Checks sin servicios |

## 3. Arquitectura propuesta

```text
┌──────────────────────────────┐
│ Test runner                  │
│ pytest / Playwright / CI     │
└──────────────┬───────────────┘
               │ MCP JSON-RPC
     ┌─────────┴──────────┐
     │                    │
     ▼                    ▼
stdio cliente       HTTP cliente
     │                    │
     └─────────┬──────────┘
               ▼
      DocuSearch MCP server
      ├─ identidad de prueba
      ├─ RBAC
      ├─ tools read-only
      └─ auditoría MCP_TOOL
               │
               ▼
        Base DocuSearch_tests
```

## 4. Entornos y aislamiento

### Base de datos

- Usar una base cuyo nombre termine en **`_tests`**.
- Nunca apuntar a la base de producción.
- `DOCUSEARCH_TEST_CONNECTION` para los checks C#.
- `DOCUSEARCH_MCP_CONNECTION` para stdio.
- `DOCUSEARCH_MCP_USER` debe ser un usuario de prueba activo.
- Para HTTP, crear tokens solo en la base de pruebas.

### Usuarios de prueba

| Usuario | Rol | Uso |
|---|---|---|
| `mcp.admin.tests` | `admin_general` | Token HTTP y lectura total |
| `mcp.auditor.tests` | `auditor` | Verificar lectura sin descarga |
| `mcp.depto.tests` | `admin_depto` | Verificar restricción departamental |
| `mcp.gestor.tests` | `gestor_documental` | Verificar hasta Confidencial |
| `mcp.consultor.tests` | `consultor` | Verificar que no ve Confidencial/Secreto |

No guardar secretos TOTP ni tokens en el repositorio.

### Datos de prueba

Crear un fixture estable con:

- documento público;
- documento de uso interno;
- documento confidencial;
- documento estrictamente secreto;
- documento de otro departamento;
- página inexistente para probar errores.

El fixture debe ser idempotente y limpiarse al terminar.

## 5. Matriz de pruebas

### 5.1 Protocolo MCP

| ID | Caso | Resultado |
|---|---|---|
| MCP-01 | `initialize` | protocolVersion y serverInfo |
| MCP-02 | `tools/list` | exactamente las 4 tools |
| MCP-03 | JSON malformado | error JSON-RPC, sin caída |
| MCP-04 | método desconocido | error controlado |
| MCP-05 | tool inexistente | error controlado |

### 5.2 Contrato de tools

| ID | Caso | Resultado |
|---|---|---|
| TOOL-01 | `list_documents` con `limit` válido | lista acotada |
| TOOL-02 | `limit=0` / `limit=999` | se normaliza |
| TOOL-03 | `search_documents` con query válida | fragmentos con page/score |
| TOOL-04 | query vacía o > 2000 | error de validación |
| TOOL-05 | `get_document_page` válida | texto de la página |
| TOOL-06 | página 0 o inexistente | error controlado |
| TOOL-07 | `rag_query` | respuesta extractiva con citas |
| TOOL-08 | ninguna tool de escritura | inventario exacto |

### 5.3 RBAC

| ID | Usuario | Caso | Resultado |
|---|---|---|---|
| RBAC-01 | consultor | listar documentos | sin Confidencial/Secreto |
| RBAC-02 | consultor | leer página confidencial | `not_authorized` genérico |
| RBAC-03 | consultor | buscar | sin fragmentos restringidos |
| RBAC-04 | admin_depto | documento de otro depto | denegado |
| RBAC-05 | gestor | confidencial propio | permitido |
| RBAC-06 | auditor | secreto | permitido en lectura |
| RBAC-07 | admin | todo | permitido |
| RBAC-08 | usuario inactivo | cualquier tool | error de identidad |

### 5.4 Autenticación HTTP

| ID | Caso | Resultado |
|---|---|---|
| AUTH-01 | sin token | 401 |
| AUTH-02 | token incorrecto | 401 |
| AUTH-03 | token expirado | 401 |
| AUTH-04 | token revocado | 401 |
| AUTH-05 | token válido | 200 |
| AUTH-06 | rate limit | 429 tras el límite |
| AUTH-07 | token almacenado | solo hash SHA-256 |

### 5.5 Auditoría

| ID | Caso | Resultado |
|---|---|---|
| AUD-01 | llamada exitosa | evento `MCP_TOOL` |
| AUD-02 | llamada denegada | evento `DENEGADO` |
| AUD-03 | error de validación | evento `ERROR` |
| AUD-04 | contenido | no se registra texto de documentos |

### 5.6 Límites y seguridad

| ID | Caso | Resultado |
|---|---|---|
| LIM-01 | respuesta de página | ≤ 20 000 caracteres |
| LIM-02 | snippet | ≤ 500 caracteres |
| LIM-03 | búsqueda | ≤ 20 resultados |
| SEC-01 | intento de elegir rol en argumentos | no existe parámetro |
| SEC-02 | error de documento restringido | no revela existencia |
| SEC-03 | inyección en query | se trata como texto |
| SEC-04 | token en logs | nunca aparece |

## 6. Cliente de pruebas propuesto

Estructura:

```text
tests/mcp/
├─ conftest.py
├─ client.py
├─ fixtures.py
├─ test_protocol.py
├─ test_tools_contract.py
├─ test_rbac.py
├─ test_auth.py
├─ test_limits.py
├─ test_audit.py
└─ smoke.py / http_smoke.py   # ya existentes
```

`client.py` debe encapsular:

- transporte stdio o HTTP;
- `initialize` y `notifications/initialized`;
- `tools/list`;
- `tools/call`;
- parseo SSE;
- timeout;
- gestión del proceso;
- cierre limpio.

Boceto:

```python
class McpClient:
    def initialize(self): ...
    def list_tools(self) -> list[dict]: ...
    def call(self, name: str, arguments: dict) -> dict: ...
    def close(self): ...
```

## 7. Integración con Playwright

Playwright sigue siendo responsable de:

- login TOTP;
- navegación;
- carga de documentos;
- aprobaciones;
- estado visual.

MCP complementa:

1. Playwright sube o modifica un documento.
2. El test consulta MCP `search_documents` o `get_document_page`.
3. Se verifica que el dato esté indexado y que el rol lo vea.
4. Un usuario `consultor` verifica que no lo vea.

Esto evita acoplar la validación de datos al DOM.

## 8. CI propuesto

### Job existente (rápido, sin servicios)

Ya ejecuta:

- build;
- checks base;
- verificador, inspector, catalogador, auditor;
- gobernanza, IA, auth;
- RAG;
- LangGraph;
- hash MCP.

### Job nuevo `mcp-contract` (con servicios)

Recomendado en runner self-hosted o entorno con Docker:

1. Levantar SQL Server de pruebas.
2. Ejecutar `--initialize-database` sobre `_tests`.
3. Ejecutar fixture de usuarios/documentos.
4. Levantar app con `Mcp__Http__Enabled=true`.
5. Crear token de prueba en la base `_tests`.
6. Ejecutar `pytest tests/mcp`.
7. Ejecutar Playwright `--grep mcp` o suite separada.
8. Destruir el entorno.

No ejecutarlo contra producción ni con datos reales.

## 9. Fases de implementación

### Fase 1 — Arnés mínimo

- `McpClient` stdio y HTTP.
- Reutilizar `smoke.py`/`http_smoke.py`.
- Tests de protocolo y tools.
- Matriz RBAC con fixture `consultor` y `admin`.

### Fase 2 — Auth completa

- Tokens expirados/revocados.
- Rate limit con límite de prueba bajo.
- Verificación de hash y último uso.
- Test de token ajeno.

### Fase 3 — Integración con la app

- Playwright crea datos.
- MCP verifica retrieval y RBAC.
- Verificación de auditoría `MCP_TOOL`.
- Casos de aprobaciones y documentos.

### Fase 4 — CI con servicios

- SQL Server efímero.
- App levantada.
- Token de prueba generado y eliminado.
- Resultados y artefactos.

## 10. Prompt para implementar

Copia este bloque en el agente de implementación:

---

Actúa como ingeniero de calidad senior en **DocuSearch AI**.

Implementa un arnés de pruebas basado en MCP, sin modificar el comportamiento de producción.

### Contexto

- Servidor MCP en `DocuSearch.Mcp/` y `Services/Mcp/`.
- Tools read-only: `list_documents`, `search_documents`, `get_document_page`, `rag_query`.
- Transportes stdio y HTTP `/mcp`.
- Auth HTTP con bearer, tokens hasheados en `McpApiTokens`.
- Base de pruebas `DocuSearch_tests`; nunca producción.
- E2E existente en `tests/e2e/`; checks C# en `tests/`.

### Objetivo

Crear `tests/mcp/client.py`, `conftest.py`, fixtures y pruebas pytest para:

1. protocolo MCP;
2. contrato de las 4 tools;
3. RBAC (consultor, admin, auditor, admin_depto, gestor);
4. auth HTTP (sin token, inválido, expirado, revocado);
5. límites y validación;
6. auditoría `MCP_TOOL`.

### Reglas

- No agregar tools de escritura.
- No aceptar identidad/rol por argumentos.
- No usar datos reales.
- No versionar tokens ni secretos.
- Limpiar procesos y sesiones.
- Tests deterministas y repetibles.
- Reutilizar los smoke existentes cuando sea posible.
- Documentar comandos en `tests/mcp/README.md`.

### Entrega

- Código del cliente y tests.
- Fixture de base/usuario/documentos.
- Comando para ejecutar en local y CI.
- Matriz de resultados.
- Limitaciones y siguientes pasos.

---

## 11. Criterios de aceptación

- [ ] `pytest tests/mcp` pasa en entorno aislado.
- [ ] `initialize`, `tools/list` y `tools/call` cubiertos.
- [ ] Las 4 tools tienen contrato y límites probados.
- [ ] Consultor no ve Confidencial/Secreto.
- [ ] Admin general sí los ve.
- [ ] Token inválido/revocado/expirado devuelve 401.
- [ ] Rate limit devuelve 429.
- [ ] Cada llamada genera `MCP_TOOL` sin texto sensible.
- [ ] No existe ninguna tool de escritura.
- [ ] El arnés no toca producción.
- [ ] CI o script reproducible documentado.

## 12. Lo que no se debe hacer

- No usar MCP para crear/borrar documentos.
- No usar el token de un administrador real.
- No exponer `/mcp` a Internet.
- No mezclar pruebas MCP con la base de producción.
- No sustituir Playwright por MCP: la UI se sigue probando en navegador.
