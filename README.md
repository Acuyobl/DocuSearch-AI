# DocuSearch AI

Control documental con **Blazor Interactive Server, ASP.NET Core 10 y SQL Server Express**.
Se compila y ejecuta con .NET y NuGet. No requiere Node.js, npm, React, Vite, Tailwind,
OpenCode ni servicios externos de IA.

## Especificaciones del agente

El paquete [Specs del agente](agente-ia/specs/001-agente-documental/spec.md)
documenta requisitos, arquitectura, modelo de datos, decisiones, inicio rápido y
tareas con trazabilidad. Índice completo en [agente-ia](agente-ia/README.md).
Incluye LangChain/Ollama local, permisos, ingesta, chat y observabilidad sin Node.js;
distingue lo implementado de las mejoras propuestas.

## Conexión ya configurada

| Propiedad | Valor |
|---|---|
| Instancia | `ICEPALFLOR\SQLEXPRESS` |
| Motor detectado | SQL Server 2019 Express, versión 15.0.2200.2 |
| Base de la aplicación | `DocuSearch` |
| Autenticación | Windows, mediante la identidad que ejecuta ASP.NET Core |
| Compatibilidad del proveedor | SQL Server 2019, nivel 150 |

La base fue creada e inicializada con cinco expedientes, cinco perfiles y seis departamentos.
No se modificaron bases ajenas. Para las pruebas se creó una base independiente `DocuSearch_tests`.
No necesitas instalar otro motor de base de datos ni configurar una contraseña SQL.

La cadena de `appsettings.json` es:

```text
Server=ICEPALFLOR\SQLEXPRESS;Database=DocuSearch;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8
```

`TrustServerCertificate=True` permite el certificado local autofirmado de esta instancia.
No desactiva el cifrado, pero omite validar su identidad: no lo uses en un despliegue remoto.
Con certificado confiable, configura `TrustServerCertificate=False`.

## Arranque

Requisitos: SDK de .NET 10, acceso de tu usuario de Windows a la instancia configurada
y, para la generación configurada, Python 3.14 con LangChain y Ollama abierto.
Instala el entorno Python una vez con `.\LangChain\setup.ps1`.

```powershell
dotnet restore
dotnet run
```

Abre <http://localhost:5068>.

```powershell
Invoke-RestMethod http://localhost:5068/health
```

La respuesta saludable indica `status: ready`, `database: SQL Server` y
`assistant: langchain-ollama-local` (o `ollama-local` en modo directo,
o `local-extractive` si desactivas la generación).
Este campo identifica la configuración, no certifica la disponibilidad del modelo:
usa **Comprobar conexión** en el asistente. Una conexión o esquema SQL no disponible devuelve HTTP 503
y un error explícito; no existe respaldo en memoria que simule éxito.

Para inicializar una instalación nueva o aplicar las migraciones pendientes:

```powershell
dotnet run -- --initialize-database
```

Este comando es idempotente: aplica migraciones y solo inserta ejemplos si no existen
usuarios. No restablece expedientes existentes.
Si cambias de equipo, configura el nombre de servidor y permite a la identidad del proceso
acceder a la base. Crear una base requiere permisos de creación; operar una base existente
requiere permisos sobre sus tablas. No concedas privilegios de administrador del servidor
para un despliegue definitivo.

Para cambiar la conexión sin editar el archivo:

```powershell
dotnet user-secrets set "ConnectionStrings:Documents" "Server=OTRO_EQUIPO\SQLEXPRESS;Database=DocuSearch;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8"
```

Los secretos de usuario se cargan en Development. También puedes proporcionar
`ConnectionStrings__Documents` mediante variables de entorno. No se leen archivos `.env`.
La aplicación web continúa siendo una demo local independientemente de la autenticación
de Windows utilizada para conectarse a SQL Server.

## Módulos conservados

| Módulo | Implementación actual |
|---|---|
| Dashboard | KPIs, distribución por categoría, ingesta mensual, almacenamiento y expedientes más consultados |
| Documentos | Catálogo, filtros, paginación, metadatos, borrado confirmado y descarga |
| Carga | Individual o lote, hasta 20 archivos con progreso y resultado independiente |
| Pipeline | Estado persistente, SHA-256 y reprocesamiento real |
| Buscador | Pestañas Buscar documentos y Asistente IA; coincidencia exacta o relevancia por términos, filtros e historial de sesión |
| Asistente integrado en el buscador | Respuestas generativas con Ollama local y citas; listas, fichas, páginas y entidades exactas; seguimiento del expediente anterior |
| Visor | Original PDF, texto por página, zoom, resumen, entidades, notas y ficha técnica |
| Reportes | Catálogo CSV, informe HTML autónomo e impresión a PDF |
| Auditoría | Eventos persistentes, filtros y exportación del historial |
| Observabilidad | Metadatos del agente, duración, errores y tokens reportados; acceso de administración general y auditoría |

En `/search`, alterna **Buscar documentos** y **Asistente IA** sin perder los
resultados ni la conversación mientras permanezcas en el buscador. Los filtros
de búsqueda no restringen el contexto del chat: el asistente consulta las fuentes
autorizadas para tu perfil. La dirección anterior `/agent` abre el mismo buscador
con el asistente seleccionado; ya no hay un módulo separado en el menú.
| Administración | Perfiles, roles, activación, departamentos, responsables y cuotas |
| Arquitectura | Descripción del sistema implementado |

Se conserva el diseño de panel ejecutivo, tema oscuro/claro, acentos índigo/violeta, cabecera
y menú lateral compacto. El dashboard incluye tarjetas de indicadores con iconografía SVG,
distribución por categorías con colores y ranking de expedientes. El diseño es adaptable a móvil
y respeta la preferencia de movimiento reducido. La interfaz está reimplementada en Razor y CSS, no es una reproducción
píxel a píxel. El JavaScript propio se ejecuta únicamente en el navegador para tema,
transferencia de archivos y recuperación de datos anteriores.

La pantalla **Documentos** se reconstruyó tomando como referencia la copia del
prototipo original: cabecera y navegación compactas, filtros en cuatro columnas,
tabla con autor/fecha, categoría, confidencialidad por color y acciones con iconos.
El filtro por año sigue disponible como opción desplegable. Se conservan la
paginación y la búsqueda por etiquetas, y se recupera la búsqueda por palabras clave.
Las acciones de descarga y reprocesamiento requieren un PDF original y los permisos
correspondientes; no se reproducen las descargas simuladas ni las afirmaciones de
OCR vectorial del prototipo.

El resto de las pantallas también toma como referencia la estructura del prototipo:
banner ejecutivo y gráficos en Dashboard; fases de procesamiento en Pipeline;
buscador central con modos contextual/exacto; conversación con sugerencias y citas;
reportes con exportaciones y métricas; bitácora filtrable; administración por pestañas;
y arquitectura por capas. Las ventanas de carga individual/lote y edición recuperan
su cabecera y distribución, y el visor usa pantalla completa con documento y panel
lateral de resumen, entidades, notas y ficha técnica. En móvil estos paneles se apilan.
Se mantienen las confirmaciones de carga, los límites actuales y los permisos en servidor.
El catálogo y el asistente actualizan su lista al entrar. Si cargas un PDF desde otra
pestaña mientras permaneces en una pantalla, usa **Actualizar documentos** para
reflejar los cambios sin recargar el navegador ni borrar la conversación.
La presentación se adapta a funciones reales: no se muestran latencias, tendencias,
porcentajes de éxito ni servicios de IA simulados. No se afirma igualdad píxel a píxel.

## Procesamiento y almacenamiento

- Límite por PDF: **25 MB**, **500 páginas**, 250 000 caracteres por página y cinco
  millones de caracteres totales.
- Lote: hasta **20 PDF**, procesados secuencialmente, cada uno con su propia transacción.
  Un fallo no revierte los archivos del lote guardados anteriormente.
  Selecciona primero los archivos en el control de carga: «Procesar lote» permanece
  desactivado mientras la lista esté vacía. Si el selector no abre en la vista previa
  de VS Code, utiliza la aplicación en Edge o Chrome en `http://localhost:5068/`.
- PdfPig extrae la **capa de texto**, no imágenes. Una página sin texto marca el expediente
  como **Requiere OCR**; no se inventa contenido.
- La ingesta mantiene resúmenes extractivos y entidades por reglas. El asistente añade
  generación con Ollama local, sin enviar documentos a terceros.
- Relevancia por términos, **no similitud vectorial ni embeddings**.
- Los cinco ejemplos conservan sus páginas de referencia y notas, pero no incluyen PDF
  original. Sus hashes históricos no prueban un archivo que no se proporcionó.
- Metadatos en tablas relacionales, páginas y notas JSON en `nvarchar(max)` con validación
  `ISJSON`, originales en `varbinary(max)`.
- Transacciones y `sp_getapplock` serializan cuotas, notas y contadores entre circuitos.
- Las cuotas cuentan solo PDF originales realmente guardados. No incluyen tamaños ficticios
  de ejemplos sin archivo. SQL Server Express 2019 tiene un límite global de **10 GB por base**
  para datos: las cuotas departamentales no amplían ese límite. El motor informa explícitamente
  si se queda sin espacio. Para una colección mayor, migra a una edición con mayor capacidad
  o diseña un almacenamiento externo protegido para los originales.
- Notas compartidas persistentes; usa **Actualizar notas** para ver cambios de otros circuitos.
  No se afirma sincronización en tiempo real.

## Asistente generativo local con LangChain y Ollama

Conexión **ASP.NET Core → Python / LangChain → Ollama → `qwen2.5-coder:7b`**.
No utiliza OpenCode, Node.js, npm ni claves de proveedores externos.
Ollama debe estar abierto y el modelo descargado. En este equipo ya está instalado;
la aplicación no instala ni descarga modelos automáticamente.

Python se ejecuta por consulta como proceso hijo con `-I`, sin shell ni servidor
Python adicional. La pregunta y las fuentes viajan por stdin, y la respuesta JSON
por stdout; no se guardan en archivos temporales. El proceso termina tras responder,
fallar o agotar el tiempo de espera. El entorno heredado se restringe y el tracing
LangSmith/LangChain se desactiva para evitar envío de consultas a telemetría externa.

Preparación única (descarga paquetes de PyPI, no documentos):

```powershell
.\LangChain\setup.ps1
```

El entorno virtual se instala fuera del proyecto en
`%LOCALAPPDATA%\DocuSearch\LangChain\.venv`. La publicación incluye el worker,
no Python ni el entorno virtual; ejecuta la preparación también al cambiar de equipo.
Si necesitas otra ubicación, configura `Ollama:PythonExecutable` con la ruta absoluta
al Python del entorno virtual (se admite `%LOCALAPPDATA%`).

LangChain usa `ChatPromptTemplate`, `ChatOllama` y salida estructurada con esquema
Pydantic. No es un agente autónomo con herramientas: no añade por sí solo memoria,
OCR ni embeddings. La recuperación y la autorización siguen en C#; LangChain
organiza la preparación de mensajes y la generación estructurada local.

Configuración en `appsettings.json`:

```json
"Ollama": {
  "Enabled": true,
  "UseLangChain": true,
  "BaseUrl": "http://127.0.0.1:11434",
  "Model": "qwen2.5-coder:7b",
  "TimeoutSeconds": 240
}
```

- Solo se admiten IP de loopback, sin credenciales, redirecciones ni proxy HTTP.
- Antes de enviar fragmentos al modelo, tanto C# como el worker comprueban `/api/show`: formato GGUF, capacidad
  generativa y ausencia de `remote_host`/`remote_model`. Se rechazan modelos cloud.
- La recuperación usa los permisos actuales; se envían como máximo seis fragmentos,
  de hasta 1000 caracteres más el indicador de truncamiento cada uno, con código,
  título y página. No se envía el PDF, la base completa ni el historial de conversación.
- Las citas JSON deben pertenecer a las fuentes enviadas. Al terminar se comprueba
  nuevamente el acceso, por si el perfil o los permisos cambiaron durante la generación.
- Las listas, fichas, metadatos, entidades y páginas explícitas mantienen la respuesta
  exacta por reglas, sin pasar por el modelo. Sin fuentes no se genera una respuesta.
  Cada mensaje identifica si se utilizó LangChain/Ollama o extracción local.
- El modelo no dispone de herramientas ni acceso directo a archivos, SQL o internet.
  La IA puede equivocarse: validar los IDs de cita no garantiza que su texto sea cierto.
  La búsqueda sigue siendo por términos, no vectorial; el contexto es parcial.
- Si Python, LangChain u Ollama falla o excede el tiempo de espera, se muestra el error y se libera la
  interfaz; no se cambia silenciosamente a otro motor o proveedor.
- Para usar solo la extracción existente, configura `Enabled: false`.
- Para elegir explícitamente la conexión C# directa anterior, configura
  `UseLangChain: false`; nunca se usa como fallback automático.

**Comprobar conexión** verifica el modelo local y la importación del worker LangChain
sin enviar documentos ni generar una respuesta. La primera
generación puede tardar más por la carga en memoria. El seguimiento usa el último
código de expediente, no una memoria conversacional completa.

Como defensa adicional a nivel del servidor Ollama, su documentación permite configurar
`OLLAMA_NO_CLOUD=1` y reiniciarlo. No se modifica ni reinicia automáticamente este servicio
compartido; las restricciones anteriores se aplican a la conexión de esta aplicación.
Consulta la [FAQ oficial](https://docs.ollama.com/faq#how-do-i-disable-ollamas-cloud-features).

## Observabilidad local sin Node.js

El módulo **Observabilidad** (`/observability`) es una implementación propia en
Blazor/C# y SQL Server; **no es Langfuse**. El servidor de Langfuse utiliza Node.js,
incluso en contenedores, por lo que no se incorpora para respetar la prohibición
de usar Node.js. No requiere Docker, servicios de telemetría ni dependencias adicionales.

- Cada llamada al agente registra al terminar una fila en `AgentTraces`: ID aleatorio,
  fecha UTC, motor/modelo, estado, duración total, categoría de error y tokens disponibles.
- No se guardan preguntas, respuestas, fragmentos, códigos de documento, usuarios,
  nombres ni mensajes de excepción en estas trazas. Los nuevos eventos `AGENT_QUERY`
  de la auditoría también omiten el texto de la pregunta. Los eventos históricos de
  auditoría se conservan sin modificación; podrían contener consultas anteriores.
- Duración: recuperación de fuentes, comprobaciones y generación; no incluye guardar
  la propia traza. Las consultas extractivas también se registran. Las comprobaciones
  de conexión y las cargas PDF no son consultas del agente y no generan estas trazas.
- Tokens: uso reportado por Ollama, transmitido desde los metadatos de `ChatOllama`
  o desde la API directa. No se estiman ni se piden al modelo. Sin generación o sin
  contadores se muestra «—», no un cero inventado. Los errores pueden no disponer de uso.
- El panel muestra las últimas 500 consultas, media y percentil 95 de duración,
  conteos por estado y suma de los tokens disponibles. **Actualizar métricas** vuelve
  a consultar la base; no hay actualización en tiempo real ni alertas automáticas.
- Solo administración general y auditoría pueden leer el panel; la comprobación se
  realiza también en el servicio C#. El selector de perfiles sigue siendo una demo.
- Las trazas persisten al reiniciar. No se borran automáticamente: las 500 filas son
  el límite de visualización, no una política de retención. No se calcula costo,
  calidad de respuestas ni tiempos separados por etapa.
- Un fallo al persistir observabilidad se informa explícitamente en la interfaz y
  en la consola con ID y tipo de fallo, sin registrar el contenido de la consulta.
  No se reporta observabilidad exitosa si SQL Server no pudo guardarla.

La migración aditiva `AgentObservability` crea únicamente la tabla e índice de
trazas. Antes de ejecutar la nueva versión contra una base existente:

```powershell
dotnet run -- --initialize-database
```

No restablece los documentos existentes. LangSmith y los exportadores externos
permanecen desactivados; no se conecta a Langfuse Cloud.

## Permisos y modo local

El selector de perfiles conserva la demostración del prototipo; **no es autenticación web**.
El servidor solo admite host e IP de loopback. No lo expongas mediante proxy, túnel o servidor
público sin sustituir el selector por identidad autenticada, autorización por usuario y TLS.
La autenticación integrada a SQL Server no autentica a los usuarios del navegador.

Las operaciones se autorizan en servidor:

- Administrador general: todos los documentos y operaciones.
- Administrador departamental: hasta Confidencial, solo su departamento; carga, edición y reproceso.
- Gestor: hasta Confidencial; carga, edición y reproceso.
- Consultor: Público y Uso Interno.
- Auditor: lectura hasta Estrictamente Secreto y auditoría; sin edición.
- Borrado y administración: solo administrador general.
- Descarga explícita: confidenciales para perfiles de gestión y secretos solo para administrador.

La misma matriz rige catálogo, búsqueda, agente, visor y reportes. Esto corrige la exposición
de fuentes restringidas que permitían algunas pantallas del prototipo.
Un PDF entregado al navegador puede guardarse desde el visor integrado: el botón restringido
de descarga **no es DRM**.

La bitácora es de solo anexado desde la aplicación y sobrevive al restablecimiento.
Un administrador de SQL Server puede modificarla; no se afirma inmutabilidad criptográfica.

## Recuperar datos del navegador anterior

No borres localStorage ni IndexedDB hasta comprobar la importación.

1. Detén la aplicación e iníciala en el mismo **origen anterior**:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run --no-launch-profile --urls http://localhost:5173
   ```

2. En el mismo navegador y perfil que usabas antes abre <http://localhost:5173/migrate.html>.
   Si usabas `127.0.0.1`, otro puerto o HTTPS, reproduce exactamente ese origen.
3. Exporta el respaldo completo. Solo lee los datos anteriores. Si no existía la base de PDF,
   usa la opción explícita **solo metadatos**, que no incluye originales.
4. Entra al sistema como administrador y abre **Administración → Migración y datos**.
5. Importa el JSON y verifica documentos, notas y originales antes de eliminar datos antiguos.

Límites del respaldo: 100 MB de JSON, 100 documentos, 1000 usuarios, 100 departamentos
y 10 000 eventos históricos. La importación es una transacción completa: un conflicto revierte
todo el respaldo. Actualiza documentos del mismo ID; no elimina un PDF del servidor por
estar ausente del respaldo. Los PDF importados se analizan de nuevo y se recalcula su hash.
El historial importado se identifica como no verificado, separado de los eventos del servidor.
Los respaldos mayores requieren división controlada antes de importarlos.

Los archivos `.txt` y `.docx` originales se conservan como requisitos históricos, no como
código, dependencias o instrucciones vigentes de compilación.

## Validación

```powershell
dotnet build
dotnet run --project .\tests\DocuSearch.Checks.csproj
```

65 comprobaciones sin servicios: datos, permisos, búsqueda, respuestas locales, CSV seguro,
HTML, PDF reales, límites exactos, esquema SQL Server y Ollama con HTTP simulado:
privacidad, contexto acotado, citas, errores y liberación del estado ocupado.
Un fallo produce código de salida no cero.

Para añadir una generación real con el modelo local, usando únicamente una fuente sintética:

```powershell
dotnet run --project .\tests\DocuSearch.Checks.csproj -- --langchain
```

Esto añade tres pruebas: disponibilidad del worker, rechazo de endpoint remoto y
generación real desde C# con LangChain y Ollama. `--ollama` permite probar también
la conexión directa anterior. Pruebas Python (ocho, sin modelo ni red):

```powershell
& "$env:LOCALAPPDATA\DocuSearch\LangChain\.venv\Scripts\python.exe" -m unittest discover -s .\LangChain -p "test_*.py" -v
```

Pruebas reales de integración en una base **exclusiva y desechable**:

```powershell
$env:DOCUSEARCH_TEST_CONNECTION = "Server=ICEPALFLOR\SQLEXPRESS;Database=DocuSearch_tests;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=8"
dotnet run --project .\tests\DocuSearch.Checks.csproj -- --database
```

La suite exige el sufijo `_tests` y restablece sus ejemplos. **No la apuntes a datos que
quieras conservar.** La base se crea automáticamente si la identidad tiene permisos.
La suite con `--database --langchain` ejecuta 79 comprobaciones: las 65 sin
servicios, tres de LangChain real y once de SQL Server. Incluye inicialización idempotente, cargas,
original persistente, metadatos, notas, reproceso, permisos reales, cuotas, agente, importación,
notas concurrentes, rollback de importaciones fallidas, contexto autorizado del consultor
y bloqueo de una respuesta si cambia el perfil durante la generación.
Se validan también la persistencia y privacidad de las trazas, la omisión de las
preguntas en nuevos eventos de auditoría y el acceso restringido al panel.
La base principal `DocuSearch` no se utiliza para pruebas destructivas.
También se comprobaron en el navegador los nueve módulos, el agente con apertura de su
página citada, la restricción del consultor y la generación de CSV.

## Migraciones y publicación

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes
dotnet ef migrations script --idempotent
dotnet publish -c Release
```

Las migraciones SQL Server y su snapshot están en `Data\Migrations`.
Para cambiar el esquema, modifica el modelo, genera `dotnet ef migrations add Nombre`
y revisa el SQL antes de aplicarlo. Respalda la base con SQL Server Management Studio
o `BACKUP DATABASE` antes de cualquier cambio. Como los PDF están en la base, el respaldo
incluye sus originales.

La publicación se genera en `bin\Release\net10.0\publish`. Se ejecuta con
`dotnet .\DocuSearch.dll` desde esa carpeta y requiere acceso Windows a la instancia.
Sigue siendo una aplicación **solo local** hasta implementar autenticación real.
No publiques la carpeta completa del proyecto como sitio estático.
