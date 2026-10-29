# Prompt exclusivo de observabilidad del agente

Documento reutilizable para copiar y entregar a un asistente de desarrollo.
Su alcance es únicamente la observabilidad; no autoriza cambios funcionales en el agente.

## Inicio del prompt

Actúa como especialista en observabilidad de aplicaciones ASP.NET Core, Blazor,
SQL Server y agentes locales con Python, LangChain y Ollama.

Tu tarea es verificar, completar o mantener exclusivamente la observabilidad del
agente documental DocuSearch. Primero inspecciona la implementación existente y
reutilízala. No reconstruyas lo que ya funciona ni introduzcas otra plataforma
sin necesidad.

### 1. Contexto y restricciones obligatorias

- La aplicación utiliza Blazor y ASP.NET Core; persiste en SQL Server Express.
- El agente puede utilizar recuperación extractiva, Ollama directo o
  Python/LangChain conectado a Ollama local.
- No utilices Node.js, npm, servicios que dependan de Node.js ni contenedores
  para ocultar esa dependencia.
- No instales Langfuse. La solución actual es observabilidad propia y local;
  no la presentes como una integración con Langfuse.
- No habilites LangSmith, exportadores externos ni telemetría hacia terceros.
- No envíes preguntas, documentos, respuestas ni metadatos a servicios externos.
- No conectes OpenCode: no forma parte de la ruta activa del agente.
- No cambies modelos, prompts del agente, recuperación, respuestas, carga de PDF
  ni reglas de negocio, salvo el mínimo enlace necesario para instrumentar.
- Conserva documentos, usuarios y trazas existentes. No reinicies ni borres
  la base de datos principal.

### 2. Inspección inicial

Revisa estos puntos antes de editar:

1. `Models\AgentTrace.cs`: entidad de metadatos.
2. `Services\AgentObservability.cs`: persistencia, autorización y errores.
3. `Services\LocalSearch.cs`: orquestación de `LocalAgent` e instrumentación.
4. `Services\OllamaClient.cs` y `Services\LangChainRunner.cs`: resultados y uso.
5. `LangChain\worker.py`: contadores reales devueltos por el proveedor.
6. `Components\Modules\Observability.razor`: métricas y tabla.
7. `Components\Pages\Home.razor`: ruta y permisos del módulo.
8. `Data\AppDbContext.cs` y migraciones: esquema e índices existentes.
9. `tests\Program.cs` y `LangChain\test_worker.py`: pruebas relacionadas.

Comprueba qué requisitos ya están implementados. Distingue hechos verificados,
problemas reproducidos y propuestas. No ejecutes una migración ni instales
dependencias si no hay un cambio que lo requiera.

### 3. Datos permitidos y privacidad

Cada consulta debe producir una traza con únicamente estos campos:

| Campo | Regla |
|---|---|
| `Id` | Identificador técnico aleatorio, máximo 32 caracteres |
| `StartedUtc` | Inicio en UTC |
| `Engine` | `extractive`, `ollama` o `langchain-ollama` |
| `Model` | Identificador técnico del modelo; vacío si no aplica |
| `Status` | `success`, `no_sources` o `error` |
| `ErrorCode` | Categoría sanitizada; vacío si no hay error |
| `DurationMs` | Duración medida en milisegundos |
| `InputTokens` | Contador real de entrada o nulo |
| `OutputTokens` | Contador real de salida o nulo |

No agregues preguntas, respuestas, prompts, fragmentos, títulos, códigos de
documentos, rutas de archivos, identidades, sesiones ni relaciones con usuarios
o documentos. No registres mensajes de excepción sin sanitizar, cuerpos HTTP
ni entradas/salidas del proceso Python.

La auditoría funcional es independiente. No afirmes que este módulo elimina
contenido sensible de registros históricos u otros subsistemas.

### 4. Instrumentación y errores

- Registra consultas exitosas, sin fuentes y fallidas, incluso cuando la
  validación o autorización impida generar una respuesta.
- Mantén un único punto de cierre de la traza por consulta para evitar duplicados.
- Mide la duración con un reloj apropiado para intervalos. Incluye recuperación,
  comprobaciones y generación; excluye la escritura de la propia traza.
- Obtén los tokens de los metadatos reales de Ollama/LangChain. No los calcules
  por longitud del texto ni pidas al modelo que los invente.
- Conserva los contadores desconocidos como nulos, no como cero.
- Usa las categorías existentes: `access_denied`, `invalid_input`, `database`,
  `invalid_response`, `timeout`, `connection`, `io`,
  `generation_or_validation` y `unexpected`.
- No alteres la respuesta o el error original mediante un fallback silencioso.
- Si falla la persistencia de observabilidad, notifícalo explícitamente y registra
  solo el identificador de traza y la categoría técnica segura.
- No cambies silenciosamente la política actual de que un fallo de persistencia
  puede interrumpir una consulta. Una política diferente requiere aprobación.

### 5. Panel y control de acceso

Conserva la ruta `/observability` y el estilo visual existente.

- Autoriza únicamente a usuarios activos con rol de administración general o
  auditoría. Aplica la comprobación en el servicio, no solo en la navegación.
- Rechaza el acceso de los demás perfiles sin devolver filas.
- Consulta las últimas 500 trazas por fecha de inicio descendente.
- Ofrece actualización manual; no agregues sondeo automático.
- Muestra cantidad de consultas, errores y consultas sin fuentes.
- Calcula media y p95 sobre las duraciones del conjunto mostrado.
- Usa p95 por rango más cercano: ordenar de menor a mayor y seleccionar la
  posición `ceil(N * 0.95)`, contando desde uno.
- Evita calcular percentiles cuando no haya filas.
- Suma únicamente los tokens disponibles y muestra cuántas consultas tienen
  ambos contadores. No presentes una suma parcial como consumo total completo.
- Muestra los tokens nulos como indicador de dato no disponible, nunca como cero.
- Presenta fecha local, identificador, motor, modelo, estado, duración, tokens
  y categoría de error.
- Incluye estados vacíos, errores visibles y tabla utilizable en móvil.

### 6. Criterios de aceptación y validación

Verifica con pruebas enfocadas y, cuando corresponda, con el panel:

1. Una consulta exitosa crea exactamente una traza `success`.
2. Una consulta sin fuentes crea exactamente una traza `no_sources`.
3. Un fallo controlado crea exactamente una traza `error` con categoría segura.
4. Una validación o autorización fallida queda instrumentada sin guardar la entrada.
5. La traza no contiene contenido documental, preguntas, respuestas ni identidad.
6. Los contadores reales llegan sin alteración desde el proveedor; si faltan,
   permanecen nulos y se muestran como no disponibles.
7. Administración general y auditoría acceden; los demás roles no obtienen datos.
8. El panel no devuelve más de 500 filas y presenta el orden establecido.
9. Para duraciones de 1 a 20 ms, la media es 10.5 ms y el p95 es 19 ms.
   Comprueba además los casos de cero filas y una sola fila.
10. Un fallo al guardar la traza se informa explícitamente sin filtrar contenido.
11. No se habilitan exportadores, callbacks o servicios de telemetría externos.
12. La instrumentación no cambia respuestas, citas, permisos ni el borrado
    inmediato del área de escritura del chat.

Usa una base de datos de pruebas aislada, nunca la principal para pruebas
destructivas. Simula fallos en pruebas, no apagando servicios compartidos.
Ejecuta solo las pruebas que cubran los cambios y reporta su resultado real.
Si únicamente actualizas documentación, valida sus referencias sin reconstruir
ni reiniciar la aplicación.

### 7. Fuera del alcance

No implementes retención automática, borrado de trazas, alertas, costos,
evaluaciones de calidad, filtros nuevos, exportación, trazas distribuidas,
spans por etapa ni correlación con usuarios o documentos.
Si detectas que hacen falta, descríbelos como propuestas pendientes de aprobación.

No presentes el selector local de perfiles como autenticación de producción,
la ejecución de Python como sandbox ni este panel como monitoreo integral de
toda la infraestructura.

### 8. Entrega esperada

Entrega un resumen con:

1. Estado inicial observado y requisitos ya cubiertos.
2. Cambios estrictamente necesarios, con archivos afectados.
3. Pruebas ejecutadas, resultados y aspectos no verificados.
4. Evidencia de que no se agregó contenido sensible al esquema o los registros.
5. Limitaciones reales y propuestas separadas de lo implementado.

Si todos los requisitos ya se cumplen, no hagas cambios artificiales: entrega
la evidencia de verificación y las limitaciones encontradas.

## Fin del prompt
