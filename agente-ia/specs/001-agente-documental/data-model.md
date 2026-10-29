# Modelo de datos y contratos del agente

**Referencia:** [AGENTE-001](spec.md) · **Fecha:** 2026-10-24.

Fuentes de verdad: [Domain](../../../Models/Domain.cs),
[AgentTrace](../../../Models/AgentTrace.cs),
[AppDbContext](../../../Data/AppDbContext.cs) y
[worker](../../../LangChain/worker.py). No todos los objetos de este documento son tablas.

## 1. Relaciones persistentes

```text
Departments.Name (clave alternativa)
   |----< Users.Department
   |----< Documents.Department

Documents.Id ---- 0..1 Files.DocumentId
Documents.PagesJson -> páginas y entidades embebidas
Documents.NotesJson -> notas embebidas

Audit: eventos independientes
AgentTraces: métricas independientes, sin FK a documento o usuario
```

Eliminar un documento elimina su original por cascada. La relación con departamentos
restringe borrados que invalidarían referencias. Auditoría y trazas no dependen por FK
del expediente, de modo que no se eliminan automáticamente al borrarlo.

## 2. Entidades documentales

### Documents / DocumentItem

| Campos | Tipo y reglas principales |
|---|---|
| `Id`, `InternalCode` | Texto hasta 100; ID clave primaria; código con índice único |
| `Title`, `Author` | Obligatorios; hasta 300 y 200 caracteres |
| `Department`, `Category` | Obligatorios; hasta 120 y 80; departamento referenciado |
| `UploadDate`, `Version` | Fecha con offset; versión obligatoria hasta 40 |
| `Confidentiality` | Hasta 40, uno de los cuatro niveles admitidos |
| `OcrStatus`, `Sha256Hash` | Estado de extracción y SHA-256 del original cuando existe |
| `FileSizeBytes`, `PageCount` | No negativos; páginas entre 0 y 500 en la base |
| `ExecutiveSummary` | Resumen extractivo; hasta 10 000 caracteres en el modelo |
| `Keywords`, `Tags` | Colecciones; servicio limita cada una a 100 elementos de hasta 200 caracteres |
| `PagesJson`, `NotesJson` | `nvarchar(max)` con restricción `ISJSON` |
| `HasOriginal` | Disponibilidad de archivo real |
| `ViewsCount`, `DownloadsCount` | Contadores persistentes |

`Pages` y `Notes` son accesores no mapeados que serializan sus listas JSON. No hay
una tabla SQL independiente por página, entidad o nota.

### Files / DocumentFile

- `DocumentId`: clave primaria y foránea a `Documents.Id`.
- `Bytes`: original PDF en `varbinary(max)`.
- Cardinalidad: un documento puede carecer de original, por ejemplo datos de demostración.
- El servicio limita a 25 MiB por original; la cuota cuenta bytes de originales reales.

### Valores embebidos

| Objeto | Campos |
|---|---|
| `DocumentPage` | `PageNumber`, `ExtractedText`, `Summary`, `Entities` |
| `DocumentEntity` | `Id`, `Text`, `Type`, `PageNumber` |
| `DocumentNote` | `Id`, `Author`, `Role`, `CreatedAt`, `Text`, `PageNumber` opcional |

Las entidades son extraídas por reglas; no equivalen a NER garantizado por un modelo.
Los ejemplos pueden tener solo determinadas páginas de referencia aunque el
metadato del documento indique un número mayor.

## 3. Usuarios, departamentos y auditoría

| Tabla | Campos principales y restricciones |
|---|---|
| `Users` | ID hasta 100, nombre hasta 200, email hasta 254 e índice único, rol válido, departamento y estado activo/inactivo |
| `Departments` | ID hasta 100, nombre hasta 120 y clave alternativa, responsable hasta 200, cuota MB positiva |
| `Audit` | ID, fecha UTC, nombre/rol, acción, código documental, detalles, estado; índice por fecha |

`Audit` sí contiene identidad y puede contener información de operaciones. No debe
confundirse con la observabilidad anónima del agente. Los nuevos eventos `AGENT_QUERY`
usan descripción fija sin pregunta. Los eventos anteriores no se reescribieron y
la política del buscador es independiente. La bitácora no es criptográficamente inmutable.

## 4. AgentTraces: observabilidad de lista cerrada

| Campo | SQL / nulabilidad | Semántica |
|---|---|---|
| `Id` | `nvarchar(32)`, no nulo, PK | GUID sin guiones, aleatorio |
| `StartedUtc` | `datetimeoffset`, no nulo, índice | Inicio de la llamada al agente |
| `Engine` | `nvarchar(40)`, no nulo | `extractive`, `ollama`, `langchain-ollama` |
| `Model` | `nvarchar(200)`, no nulo | Modelo configurado; vacío para ruta extractiva |
| `Status` | `nvarchar(20)`, no nulo | `success`, `no_sources`, `error` |
| `ErrorCode` | `nvarchar(40)`, no nulo | Categoría; vacío si no hay error |
| `DurationMs` | `bigint`, no nulo | Tiempo antes de guardar la traza |
| `InputTokens` | `bigint`, nulo permitido | Uso de entrada reportado por el motor |
| `OutputTokens` | `bigint`, nulo permitido | Uso de salida reportado por el motor |

Categorías actuales: `access_denied`, `invalid_input`, `database`, `invalid_response`,
`timeout`, `connection`, `io`, `generation_or_validation`, `unexpected`.
La categoría puede derivarse de una excepción interna; no almacena su mensaje.

No existen campos para prompt, respuesta, fragmento, documento, perfil, sesión,
correo o nombre. Las consultas fallidas pueden tener tokens nulos aunque haya
habido trabajo de inferencia antes del fallo.

El panel consulta como máximo 500 filas por fecha descendente. El p95 utiliza
el elemento de rango `ceil(N × 0.95)` de duraciones ordenadas, con índice basado
en uno. Media, conteos y sumas corresponden a esta misma ventana.
No hay retención automática ni separación de tiempos por etapa.

## 5. Estado en memoria, no tablas

| Objeto | Duración y contenido |
|---|---|
| `LocalSession` | Circuito Blazor; `UserId` y `LastDocumentCode` |
| Mensajes del chat | Componente; usuario/asistente, texto, fuentes y etiqueta del motor |
| `WorkspaceState` | Circuito; snapshot, perfiles, estado ocupado, aviso y error |
| `SearchHit` | Resultado: documento, número de página, fragmento y score |
| `AgentAnswer` | Texto, fuentes, proveedor y tokens de entrada/salida opcionales |
| `AppSnapshot` | Actor, documentos autorizados, usuarios, departamentos y auditoría permitida |

No se persiste la conversación. Limpiar chat borra mensajes y último código;
cambiar perfil también elimina el contexto del expediente.

## 6. Contrato C# → worker Python

Transporte: stdin JSON UTF-8, sin argumentos que contengan la pregunta.
Ejemplo sintético:

```json
{
  "base_url": "http://127.0.0.1:11434",
  "model": "qwen2.5-coder:7b",
  "timeout": 240,
  "question": "¿Qué disponibilidad establece el contrato?",
  "sources": [
    {
      "id": 1,
      "code": "TEST-2026-001",
      "title": "Contrato sintético",
      "page": 4,
      "text": "La disponibilidad garantizada es de 99.99%."
    }
  ],
  "system": "Instrucciones documentales de solo lectura definidas por el servidor."
}
```

### Validaciones del worker

- Pydantic estricto y campos adicionales prohibidos.
- `question`: 1 a 2000 caracteres; C# además rechaza solo espacios.
- `timeout`: entero 1 a 600.
- `model`: 1 a 200 caracteres; no se admiten nombres cloud.
- `system`: 1 a 4000 caracteres, controlado por C#.
- `sources`: 1 a 6; IDs contiguos de 1 a N, página al menos 1.
- `code` hasta 200, `title` hasta 1000, `text` hasta 1001 caracteres.
- URL absoluta HTTP/HTTPS, IP loopback, sin credenciales, query, fragment ni ruta
  distinta de raíz. Redirecciones y proxies heredados desactivados.
- Metadatos `/api/show`: GGUF, capacidad `completion`, sin `remote_host`/`remote_model`.

El modelo recibe solo `question`, fuentes y prompt; los campos de conexión no forman
parte del contenido documental. La entrada del worker se limita a 100 000 bytes.

## 7. Contrato worker → C#

stdout contiene un objeto JSON y stderr se reserva para diagnóstico:

```json
{
  "answer": "La disponibilidad establecida es de 99.99%.",
  "citations": [1],
  "input_tokens": 350,
  "output_tokens": 24
}
```

Los números del ejemplo son ilustrativos, no resultados de una ejecución.

- El modelo solo genera `answer` y `citations` mediante el esquema `Answer`.
- Python añade los tokens desde `AIMessage.usage_metadata`, no desde texto inventado
  por el modelo. Los contadores pueden ser `null`.
- En la ruta LangChain, `citations` tiene entre 1 y 6 enteros. Todos deben pertenecer
  al contexto; texto no vacío, máximo 10 000 caracteres.
- C# vuelve a validar índices y elimina duplicados conservando el orden.
- Las fuentes finales se reconstruyen desde las enviadas, nunca desde nombres
  o rutas generadas por el LLM.
- Respuestas truncadas/incompletas o fallos de parseo se rechazan.
- Una salida no cero, vacía, demasiado grande o fuera de tiempo causa error explícito.

`--check` solo importa y comprueba el worker:

```json
{"status":"ready","framework":"LangChain","provider":"ollama-local"}
```

No genera tokens ni crea trazas del agente.

## 8. Configuración y migraciones

La sección `Ollama` incluye `Enabled`, `UseLangChain`, `BaseUrl`, `Model`,
`TimeoutSeconds` y la opción `PythonExecutable`. Valor predeterminado del último:
`%LOCALAPPDATA%\DocuSearch\LangChain\.venv\Scripts\python.exe`.

Configuración no es disponibilidad: `/health` verifica SQL y describe el motor
configurado, pero no ejecuta inferencia ni garantiza que el worker esté operativo.

Migraciones: [inicial](../../../Data/Migrations/20261024160407_InitialSqlServer.cs)
y [observabilidad](../../../Data/Migrations/20261024174625_AgentObservability.cs).
Aplicar de manera aditiva; nunca usar el reinicio de ejemplos para actualizar esquema.
