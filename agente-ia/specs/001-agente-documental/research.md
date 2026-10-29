# Investigación y decisiones del agente

**Fecha de corte:** 2026-10-24 · **Referencia:** [AGENTE-001](spec.md).

Este registro recoge decisiones ya tomadas a partir de las restricciones del usuario,
la implementación y las verificaciones de la sesión. No afirma que las alternativas
descartadas sean técnicamente imposibles fuera de esas restricciones.

## ADR-01. Migración fuera de Node.js

- **Necesidad:** conservar control documental y diseño sin cadena Node.js.
- **Decisión:** Blazor Interactive Server + ASP.NET Core 10 + CSS y JavaScript mínimo
  en navegador para tema, archivos y utilidades.
- **Descartado:** mantener React/Vite/npm como compilación activa.
- **Consecuencia:** construir con .NET/NuGet; no usar paquetes Node para las nuevas funciones.
- **Evidencia:** [proyecto .NET](../../../DocuSearch.csproj),
  [Program](../../../Program.cs), [interfaz](../../../Components/Pages/Home.razor).

La regla excluye Node.js, no todo JavaScript ejecutado por el navegador.

## ADR-02. SQL Server local en lugar de PostgreSQL

- **Contexto:** se propuso PostgreSQL, pero el equipo dispone de `ICEPALFLOR\SQLEXPRESS`.
- **Decisión:** SQL Server Express 2019 con autenticación Windows y EF Core.
- **Consecuencia:** configuración y migraciones específicas para SQL Server,
  compatibilidad 150 y límite global de 10 GB para datos.
- **Persistencia:** originales en la misma base, no en almacenamiento del navegador.
- **Evidencia:** [contexto EF](../../../Data/AppDbContext.cs),
  [migraciones](../../../Data/Migrations), [configuración](../../../appsettings.json).

## ADR-03. Recuperar antes de generar

- **Decisión:** C# obtiene exclusivamente fuentes autorizadas y determina intención;
  el modelo solo redacta a partir de un contexto acotado.
- **Motivo:** preservar RBAC, referencias y respuestas exactas.
- **Alternativa no implementada:** búsqueda vectorial con embeddings.
- **Consecuencia:** recuperación por proporción de términos y coincidencia exacta;
  no prometer comprensión semántica ni cobertura completa del documento.
- **Evidencia:** [LocalSearch/LocalAgent](../../../Services/LocalSearch.cs).

El score es una proporción de términos coincidentes, no una probabilidad de
veracidad ni un grado de similitud vectorial.

## ADR-04. Ollama local como proveedor

- **Decisión:** usar el modelo ya disponible `qwen2.5-coder:7b`.
- **Motivo:** ningún documento debe salir del equipo.
- **Controles:** IP loopback, sin proxy ni redirects, metadatos locales GGUF y
  capacidad `completion`; rechazar atributos remotos y nombres cloud.
- **Consecuencia:** el servicio y modelo tienen que estar disponibles localmente;
  no se descargan modelos ni se usa nube al fallar.
- **Evidencia:** [cliente](../../../Services/OllamaClient.cs),
  [worker](../../../LangChain/worker.py).

La dirección localhost por sí sola no prueba que un modelo sea local: algunos
servidores locales pueden delegar a proveedores externos.

## ADR-05. LangChain en Python, no LangChain.js

- **Evolución:** inicialmente se eligió C# directo; posteriormente se autorizó
  expresamente añadir LangChain sin Node.js.
- **Decisión:** LangChain Core + integración Ollama en un worker Python 3.14.
- **Uso real:** `ChatPromptTemplate`, composición de runnables, `ChatOllama`,
  salida estructurada Pydantic.
- **No utilizado:** `create_agent`, herramientas arbitrarias o un agente de clima.
- **Motivo:** el caso documental de solo lectura no requiere autonomía para ejecutar acciones.
- **Consecuencia:** añade dependencia de Python y arranque de proceso por consulta,
  pero evita un servidor HTTP Python y nuevas credenciales.
- **Evidencia:** [requirements](../../../LangChain/requirements.txt),
  [setup](../../../LangChain/setup.ps1), [worker](../../../LangChain/worker.py).

LangChain facilita organizar y extender el flujo; no aumenta por sí mismo la
inteligencia del modelo ni añade automáticamente OCR, memoria o embeddings.

## ADR-06. Integración mediante proceso hijo

- **Decisión:** C# ejecuta el Python del entorno virtual por ruta explícita,
  con `-I`, sin shell, entrada/salida JSON y UTF-8.
- **Motivo:** no mantener otro puerto web ni permitir resolver intérpretes vía npm.
- **Controles:** entorno heredado reducido, límites de tamaño, timeout y terminación
  del árbol del proceso hijo en la limpieza.
- **Consecuencia:** `-I` aísla importación/configuración Python, no permisos del SO.
- **Evidencia:** [LangChainRunner](../../../Services/LangChainRunner.cs).

## ADR-07. Citas estructuradas y ruta exacta

- **Decisión:** el servidor asigna IDs y valida los IDs devueltos. Listados, entidades,
  fichas y páginas se mantienen deterministas.
- **Motivo:** no delegar conteos ni extracción exacta a un modelo con contexto parcial.
- **Consecuencia:** una respuesta inválida se bloquea; la interfaz identifica el motor real.
- **Límite:** una cita válida demuestra pertenencia al contexto, no que todas las
  afirmaciones del texto estén respaldadas por ella.
- **Evidencia:** [cliente](../../../Services/OllamaClient.cs),
  [chat](../../../Components/Modules/AgentChat.razor).

## ADR-08. OpenCode no activo

- **Contexto:** se pidió explorar OpenCode CLI y se identificó una versión nativa.
- **Resultado:** la prueba de inferencia por CLI no quedó verificada por restricciones
  de autorización de ejecución; no se reemplazó la integración funcional.
- **Decisión vigente:** LangChain → Ollama; sin OpenCode en el flujo de la aplicación.
- **Consecuencia:** no anunciar compatibilidad OpenAI en `localhost:4096/v1` por
  suposición ni tratar un HTTP 200 con HTML como API válida.

## ADR-09. Observabilidad propia, no Langfuse

- **Necesidad:** duración, modelo, tokens y errores, todo local y sin contenido.
- **Conflicto:** el servidor/panel Langfuse utiliza Node.js, también en contenedores.
- **Decisión del usuario:** mantener cero Node.js y seleccionar alternativa compatible.
- **Implementación:** tabla `AgentTraces`, servicio C# y panel Blazor restringido.
- **Consecuencia:** sin API Langfuse, sin árbol distribuido de spans, sin evaluaciones,
  sin alertas, sin retención automática ni costos calculados.
- **Evidencia:** [modelo](../../../Models/AgentTrace.cs),
  [servicio](../../../Services/AgentObservability.cs),
  [panel](../../../Components/Modules/Observability.razor).

## ADR-10. Trazas sin contenido y tokens reportados

- **Decisión:** lista cerrada de campos anónimos; categorías de error, no mensajes.
- **Tokens:** desde `AIMessage.usage_metadata` o contadores de la API Ollama;
  ausentes como `null`, nunca estimados.
- **Auditoría:** nuevos eventos del agente omiten la pregunta; no reescribir registros
  históricos ni confundir observabilidad con toda la bitácora.
- **Consecuencia:** no es posible reconstruir la conversación desde `AgentTraces`.
- **Fallo de persistencia:** informar explícitamente; no simular registro exitoso.
- **Evidencia:** [orquestador](../../../Services/LocalSearch.cs),
  [pruebas Python](../../../LangChain/test_worker.py),
  [pruebas .NET](../../../tests/Program.cs).

## ADR-11. UI conservada y estados explícitos

- **Decisión:** reconstruir diseño a partir del prototipo sin reintroducir su stack.
- **Correcciones incluidas:** selección y confirmación de carga, refresco de documentos,
  editor vacío al enviar y borrador nuevo conservado, fuentes al visor.
- **Consecuencia:** no se promete igualdad píxel a píxel ni sincronización entre pestañas.
- **Evidencia:** [carga](../../../Components/Modules/UploadDialog.razor),
  [catálogo](../../../Components/Modules/DocumentManagement.razor),
  [chat](../../../Components/Modules/AgentChat.razor).

## Referencias oficiales utilizadas

Estas referencias explican productos/protocolos; las versiones locales y el código
del proyecto prevalecen para describir lo realmente implementado.

- [LangChain: ChatOllama](https://docs.langchain.com/oss/python/integrations/chat/ollama)
- [Ollama: API de chat](https://docs.ollama.com/api/chat)
- [Ollama: FAQ y desactivación de cloud](https://docs.ollama.com/faq)
- [OpenCode: CLI](https://opencode.ai/v2/docs/cli/)
- [Langfuse: despliegue local](https://langfuse.com/self-hosting/deployment/docker-compose)
- [Langfuse: Dockerfile del servidor](https://github.com/langfuse/langfuse/blob/main/web/Dockerfile)

No son destinos de envío de documentos ni dependencias de ejecución de la aplicación.
