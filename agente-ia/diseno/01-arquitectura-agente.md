# Arquitectura del asistente documental

## Componentes

| Componente | Responsabilidad |
|---|---|
| `AgentChat.razor` | Conversación y enlaces a fuentes; texto renderizado con escape Razor |
| `LocalAgent` | Identificar intención, código, página y referencia al expediente anterior |
| `LocalSearch` | Coincidencia exacta y relevancia por proporción de términos |
| `TextAnalysis` | Normalización, resumen extractivo y entidades por reglas |
| `DocumentService` | Lectura autorizada y auditoría en SQL Server |
| `LocalSession` | Perfil y referencia contextual por circuito Blazor |
| `OllamaClient` | Modelo local, contexto acotado, citas y contadores reportados |
| `LangChainRunner` / `worker.py` | Proceso Python aislado con composición LangChain y salida estructurada |
| `AgentObservability` | Trazas SQL sin contenido; lectura restringida |
| `Observability.razor` | Duraciones, estados y tokens de las últimas 500 consultas |

## Flujo

1. Validar consulta de 1 a 2000 caracteres.
2. Obtener perfil vigente y documentos permitidos desde el servidor.
3. Registrar `AGENT_QUERY` con descripción fija, sin texto de la consulta.
4. Detectar código, intención de listado/resumen/entidades/página o búsqueda general.
5. Consultar fuentes locales. Mantener listas, fichas, entidades y páginas por reglas;
   para otras preguntas con fuentes, usar LangChain/Python y Ollama local.
6. Validar respuesta y citas; reautorizar fuentes tras generar.
7. Persistir metadatos de ejecución, sin pregunta, respuesta ni identidad.
8. Permitir abrir la página citada mediante una operación nuevamente autorizada.

Si no existen fuentes accesibles, se informa sin inventar una respuesta.
Cambiar de perfil o limpiar la conversación borra el contexto anterior.

## Límites

Hay generación con un LLM local, pero no embeddings, OCR de imágenes, herramientas
del modelo ni modificaciones documentales desde el chat. La recuperación sigue
siendo léxica y el contexto es parcial. El texto de los expedientes se trata como
datos; validar citas no garantiza exactitud factual.

El framework es LangChain Python, no LangChain.js. OpenCode no está activo.
La observabilidad es propia en Blazor/SQL Server, no Langfuse; se mantiene cero Node.js.

## Permisos

La matriz se define en [Permissions.cs](../../Services/Permissions.cs) y se reutiliza
en la interfaz y los servicios. El administrador departamental solo accede a su
departamento. El consultor no accede a confidenciales ni secretos.

El selector de perfiles es una demostración local. Antes de publicar en una red,
debe sustituirse por una identidad autenticada y autorización por usuario.

## Pruebas

La suite en [tests](../../tests/Program.cs) cubre permisos y recuperación de fuentes.
Con SQL Server verifica consultas con citas, seguimiento contextual y bloqueo de
documentos restringidos. Requisitos y comandos en [README](../../README.md).

Especificación completa y contratos:
[Specs del agente](../specs/001-agente-documental/spec.md).
