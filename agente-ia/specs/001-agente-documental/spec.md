# Especificación del agente documental local

**Identificador:** AGENTE-001  
**Versión:** 1.0  
**Fecha de corte:** 2026-10-24  
**Estado:** línea base de la implementación y requisitos de evolución.

Este paquete utiliza la estructura documental solicitada. No presupone la instalación
de Spec Kit ni de otra herramienta generadora. Los requisitos describen el comportamiento
actual salvo donde se indica expresamente una capacidad pendiente.

## 1. Objetivo

Permitir consultar expedientes PDF mediante un asistente de solo lectura, con
fuentes verificables, permisos en servidor, generación local y observabilidad
sin contenido documental. Mantener la aplicación sin Node.js en compilación,
ejecución, integraciones y servicios auxiliares.

**Usuarios:** administrador general, administrador departamental, gestor documental,
consultor y auditor.

**Resultado esperado:** una respuesta basada en fuentes accesibles con acciones para
abrir documento y página, o un mensaje explícito de ausencia de fuentes o fallo.
Validar una cita no garantiza la exactitud factual de una respuesta generativa.

## 2. Alcance y restricciones

| Incluido | Excluido de esta versión |
|---|---|
| Blazor Interactive Server y servicios ASP.NET Core 10/C# | Node.js, npm y servidor JavaScript |
| SQL Server Express local con autenticación Windows | PostgreSQL como proveedor activo |
| Python aislado, LangChain Core y ChatOllama | LangChain.js, servicio Python HTTP y agente autónomo |
| Ollama y modelo local `qwen2.5-coder:7b` | Proveedores remotos, modelos cloud y descarga automática |
| Búsqueda por términos y coincidencia exacta | Embeddings, base vectorial y búsqueda semántica real |
| Resúmenes generativos del chat y extractivos de ingesta | OCR de imágenes |
| Citas, seguimiento por último código y visor | Memoria conversacional completa o persistente |
| Panel propio de observabilidad Blazor/SQL Server | Langfuse, LangSmith tracing y exportadores externos |

OpenCode CLI fue explorado, pero no forma parte del flujo activo. No se incorpora
Langfuse porque su servidor utiliza Node.js. La observabilidad actual no implementa
la API ni todas las capacidades de Langfuse.

## 3. Historias de usuario y aceptación

### HU-01. Incorporar fuentes

Como usuario autorizado para escritura, quiero cargar PDF y saber si se guardaron.

- **CA-01:** elegir un archivo o un lote muestra nombres y estado antes de procesar.
- **CA-02:** sin selección no se habilita el procesamiento.
- **CA-03:** cada PDF guardado muestra confirmación; un fallo se comunica por archivo.
- **CA-04:** el original y metadatos persisten en SQL Server. Un lote no revierte
  archivos anteriores por el fallo de otro.
- **CA-05:** si un PDF carece de texto, se marca la necesidad de OCR; no se simula extracción.
- **CA-06:** al entrar a catálogo/asistente se actualiza la lista. Los cambios
  realizados desde otra pestaña se recuperan con **Actualizar documentos**.

### HU-02. Conversar con fuentes

Como lector, quiero preguntar sin perder visibilidad de lo enviado.

El asistente está integrado en `/search`, en la pestaña **Asistente IA** junto
a **Buscar documentos**. Alternar ambas conserva resultados y conversación
mientras el componente permanece abierto. `/agent` es un acceso compatible
al mismo buscador con el asistente seleccionado, no un módulo independiente.
Los filtros del buscador no se aplican al chat; este recupera fuentes autorizadas
para el perfil activo.

- **CA-07:** al enviar, la pregunta se añade al historial visible y el editor se vacía
  inmediatamente, antes de recibir la respuesta.
- **CA-08:** un borrador nuevo escrito durante la espera no se borra al terminar.
- **CA-09:** se muestra el estado ocupado y se impide otro envío desde el botón mientras
  la operación está en curso.
- **CA-10:** la respuesta identifica su motor: extracción local o LangChain/Ollama.
- **CA-11:** las fuentes abren el visor en la página citada y vuelven a autorizar el acceso.
- **CA-12:** nueva conversación limpia mensajes y último código del circuito.

### HU-03. Responder con el motor apropiado

- **CA-13:** listados, fichas, metadatos, páginas y entidades explícitas conservan
  la ruta determinista, sin reformulación por el modelo.
- **CA-14:** resumen o pregunta documental con fuentes puede usar generación local.
- **CA-15:** una consulta sin fuentes accesibles informa la limitación sin llamar al LLM.
- **CA-16:** el seguimiento «resume ese expediente» utiliza el último código cuando
  existe; no se envía el historial completo a Ollama.
- **CA-17:** una respuesta generativa vacía, incompleta o con IDs de cita fuera del
  contexto enviado se bloquea con error explícito.

### HU-04. Respetar permisos y privacidad

- **CA-18:** la recuperación se limita al catálogo autorizado para el perfil activo.
- **CA-19:** después de generar se comprueba el perfil y la disponibilidad autorizada
  de las fuentes recuperadas; si cambia el acceso, no se devuelve la respuesta.
- **CA-20:** la aplicación rechaza endpoints no loopback y modelos cloud o no confirmados
  como GGUF local con capacidad de generación.
- **CA-21:** ningún modelo recibe herramientas para ejecutar comandos, modificar
  archivos, consultar SQL o navegar por internet.
- **CA-22:** fallos de Python, LangChain, Ollama o citas no activan proveedores
  externos ni un fallback silencioso.

### HU-05. Observar el agente sin guardar contenido

Como administrador general o auditor, quiero analizar metadatos de ejecución.

- **CA-23:** cada llamada que termina en el agente intenta persistir una traza con
  duración, estado, motor/modelo, categoría de error y tokens disponibles.
- **CA-24:** la tabla de trazas no contiene pregunta, respuesta, fragmento, código
  documental, usuario ni mensaje de excepción.
- **CA-25:** los tokens vienen del uso reportado por Ollama; ausencia se representa
  con un valor nulo y «—», no con una estimación.
- **CA-26:** el panel muestra las últimas 500 trazas, promedio, p95 y agregados de esa
  ventana; actualizar vuelve a leer SQL Server.
- **CA-27:** el servicio deniega lectura de trazas a consultor, gestor y administrador
  departamental, aunque accedan directamente a la ruta.
- **CA-28:** si falla guardar la traza, se informa el fallo; no se simula observabilidad
  exitosa. La respuesta de la consulta puede quedar interrumpida por ese fallo.

## 4. Requisitos funcionales

| ID | Requisito | Aceptación |
|---|---|---|
| RF-01 | Ingesta PDF persistente, con límites y resultado por archivo | CA-01 a CA-06 |
| RF-02 | Editor e historial del chat con envío y estado ocupado | CA-07 a CA-10, CA-12 |
| RF-03 | Fuentes estructuradas y navegación al visor | CA-11, CA-17 |
| RF-04 | Enrutamiento extractivo/generativo explícito | CA-13 a CA-16 |
| RF-05 | Autorización previa y posterior a generación | CA-18, CA-19 |
| RF-06 | Ejecución exclusivamente local, sin herramientas del modelo | CA-20 a CA-22 |
| RF-07 | Observabilidad persistente de solo metadatos | CA-23 a CA-28 |

## 5. Matriz de acceso

| Perfil | Lectura documental | Escritura documental | Observabilidad/auditoría |
|---|---|---|---|
| Administrador general | Todos los niveles | Sí | Sí |
| Administrador departamental | Hasta confidencial y solo su departamento | Sí, dentro de su alcance | No |
| Gestor documental | Hasta confidencial | Sí, dentro de su alcance | No |
| Consultor | Público y uso interno | No | No |
| Auditor | Todos los niveles | No | Sí |

Descargar es un permiso diferente de leer: secretos solo los descarga administración
general; confidenciales requieren además un rol de escritura. Se necesita original
para descargar o reprocesar. El visor no proporciona DRM.

**Advertencia:** el inicio de sesión exige un código TOTP de la app autenticadora
(`/login` y alta en `/enroll`); los secretos y roles viven en `Auth:Users`.
La identidad Windows se usa para SQL Server, no para autenticar al navegador.

## 6. Límites medibles

| Elemento | Límite actual |
|---|---|
| Consulta | No vacía; máximo 2000 caracteres, validada en servidor |
| PDF | 25 × 1024 × 1024 bytes, 500 páginas |
| Texto extraído | 250 000 caracteres/página y 5 000 000 por PDF |
| Lote | 20 archivos, secuenciales |
| Contexto del LLM | Hasta seis fuentes distintas por documento/página |
| Fragmento enviado | Hasta 1000 caracteres más indicador de truncamiento |
| Contexto/modelo | `num_ctx=8192`, `num_predict=600`, temperatura 0 |
| Respuesta | Texto hasta 10 000 caracteres y citas válidas |
| Worker | Entrada hasta 100 000 bytes; stdout/stderr hasta 100 000 caracteres cada uno |
| Espera configurada | 240 segundos; rango permitido 1 a 600 por operación configurada |
| Panel de trazas | Últimas 500; no limita las filas almacenadas |
| SQL Express 2019 | 10 GB de datos por base; cuotas no amplían ese límite |

El timeout no es un SLA global ni un tiempo máximo garantizado de extremo a extremo:
SQL, verificaciones y guardado de traza son operaciones adicionales.

## 7. Requisitos no funcionales

- **RNF-01 Localidad:** host/IP de entrada restringidos a loopback; sin proveedores de IA remotos.
- **RNF-02 Dependencias:** .NET/NuGet y Python/pip; nunca Node.js/npm para la aplicación.
- **RNF-03 Transparencia:** distinguir OCR, búsqueda por términos, generación y ejemplos
  sin original; no presentar capacidades simuladas.
- **RNF-04 Errores:** mostrar fallos con el mecanismo de estado de la aplicación y liberar
  el estado ocupado; no respuestas exitosas ficticias.
- **RNF-05 Persistencia:** documentos, originales, auditoría y trazas sobreviven reinicios.
- **RNF-06 Interfaz:** tema claro/oscuro, uso móvil, tablas desplazables localmente,
  estados de carga y respeto de movimiento reducido.
- **RNF-07 Privacidad de trazas:** metadatos permitidos por lista cerrada; sin exportación externa.

## 8. Limitaciones y riesgos conocidos

- Recuperación léxica: puede no encontrar sinónimos ni intención compleja. El contexto
  enviado es parcial, no el PDF completo.
- El prompt trata documentos como datos, pero no demuestra inmunidad a inyección
  de instrucciones ni ausencia de alucinaciones.
- `python -I` y la eliminación de herramientas no constituyen un sandbox del sistema operativo.
- Las trazas protegen su propio contenido, no sustituyen una auditoría integral de todos
  los logs. Eventos históricos y el buscador pueden tener políticas diferentes.
- No hay retención automática de trazas, alertas, streaming del chat, cancelación
  manual, memoria persistente, costo por token ni evaluación automática de calidad.
- La actualización entre pestañas es manual; no se promete sincronización en tiempo real.
- No se admite publicar a una red antes de implementar autenticación real, TLS y
  políticas de despliegue.

## 9. Documentos relacionados

- [Plan técnico](plan.md)
- [Modelo de datos y contratos](data-model.md)
- [Investigación y decisiones](research.md)
- [Inicio rápido](quickstart.md)
- [Tareas y verificación](tasks.md)
