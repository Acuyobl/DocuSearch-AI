# Flujo de acceso por rol (RBAC) del agente

## Regla principal

El agente **solo puede ver y citar expedientes que el usuario autenticado puede leer**. No se filtra en el prompt: se filtra antes de recuperar y se vuelve a comprobar antes de responder.

## Niveles y roles

| Rol | Niveles legibles | Restricción adicional |
|---|---|---|
| `admin_general` | Público, Uso Interno, Confidencial, Estrictamente Secreto | Ninguna |
| `auditor` | Todos | No descarga secretos; solo lectura |
| `admin_depto` | Público, Uso Interno, Confidencial | Solo su departamento |
| `gestor_documental` | Público, Uso Interno, Confidencial | Sin restricción departamental para lectura |
| `consultor` | Público, Uso Interno | No lee Confidencial ni Secreto |
| Rol desconocido | Ninguno | Falla cerrado |

Fuente: `Services/Permissions.cs`.

## Flujo de una consulta

```text
Cookie/claims → LocalSession → AppUser (rol y departamento)
   │
   ▼
DocumentService.LoadAsync()
   ├─ Readable(): filtra por niveles y departamento
   ├─ carga páginas y vectores solo de documentos legibles
   └─ devuelve AppSnapshot con actor + documentos permitidos
   │
   ▼
RagQueryService
   ├─ retrieval recibe SOLO IDs del snapshot
   ├─ LangGraphRagService vuelve a filtrar por Permissions.CanRead
   ├─ las fuentes se mapean a documentos del snapshot
   ├─ AG-02 valida documento, página, texto y permisos
   └─ StillAuthorizedAsync() recarga y comprueba que no cambió el acceso
   │
   ▼
Respuesta: solo fuentes autorizadas o mensaje de falta de acceso
```

## Defensas implementadas

1. **Filtro en origen:** `DocumentService.Readable` no devuelve expedientes fuera del rol.
2. **IDs explícitos:** el retriever y Pinecone reciben únicamente IDs legibles.
3. **Filtro en LangGraph:** antes de serializar fuentes se comprueba `Permissions.CanRead`.
4. **AG-02:** una cita de documento no legible o página inexistente invalida la respuesta.
5. **Revalidación final:** si el rol cambia durante la consulta, se devuelve `access_denied` y no se muestran fuentes.
6. **UI:** el asistente y el buscador trabajan sobre `State.Data.Documents`, ya filtrado.
7. **Auditoría:** las operaciones denegadas y las consultas RAG quedan registradas sin exponer contenido si `AuditContent=false`.

## Cómo probar con un rol bajo

1. Crear un usuario con `Role="consultor"` (o usar el fixture de pruebas).
2. Iniciar sesión con ese usuario.
3. Pedir un resumen o una pregunta sobre un documento `Confidencial` o `Estrictamente Secreto`.
4. Resultado esperado:
   - sin citas de ese documento,
   - sin texto del documento,
   - mensaje de falta de fuentes o acceso denegado.

Pruebas existentes de política en `tests/Program.cs`:

- El consultor no lee archivos confidenciales/secretos.
- El consultor sí lee documentos internos.
- El auditor lee secretos pero no los descarga.
- El admin departamental solo lee su departamento.
- Un rol desconocido falla cerrado.

## Prueba de integración real

El bloque `--database` crea un consultor, cambia la sesión a él y comprueba que el asistente solo recibe fuentes autorizadas. Para ejecutarlo:

```powershell
$env:DOCUSEARCH_TEST_CONNECTION = "Server=...;Database=DocuSearch_tests;..."
docker run --rm -v "${PWD}:/src:ro" ds-app-langchain:latest bash -c 'mkdir /t && cp -a /src/. /t/ && rm -rf /t/bin /t/obj /t/tests/bin /t/tests/obj && cd /t && dotnet run --project tests/DocuSearch.Checks.csproj -c Debug -- --database'
```

## Pendiente

- Añadir una prueba E2E navegable con un usuario `consultor` de demo; hoy la matriz de roles está cubierta por pruebas unitarias y la integración SQL.
- No crear usuarios de prueba con secretos TOTP en el repositorio.
