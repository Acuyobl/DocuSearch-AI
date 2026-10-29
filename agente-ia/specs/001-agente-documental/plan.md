# Plan técnico del agente

**Referencia:** [AGENTE-001](spec.md) · **Fecha:** 2026-10-24.

Este plan describe la arquitectura implementada y el orden reproducible de construcción.
El estado de ejecución y los pendientes se mantienen en [tasks.md](tasks.md).

## 1. Arquitectura

```text
Navegador local
  Blazor Interactive Server / AgentChat
       |
  LocalAgent (C#)
       |---- DocumentService / Permissions ---- SQL Server Express
       |       recuperación autorizada          PDF, páginas, metadatos, auditoría
       |
       |---- ruta exacta -------------------- respuesta extractiva
       |
       |---- OllamaClient ---- LangChainRunner
       |                          |
       |                   Python -I / worker.py
       |                   stdin JSON -> LangChain -> ChatOllama
       |                          |
       |                   Ollama 127.0.0.1:11434
       |                   qwen2.5-coder:7b local
       |                          |
       |                   stdout JSON + tokens reportados
       |
       |---- validación de citas + reautorización
       |---- AgentObservability ---------------- SQL / AgentTraces
       |
  Respuesta + fuentes -> visor con autorización
```

Alternativas configurables: desactivar generación (`Enabled=false`) o seleccionar
explícitamente C# → Ollama (`UseLangChain=false`). No son fallback automático.

## 2. Tecnologías y despliegue

| Capa | Selección |
|---|---|
| Interfaz y orquestación | Blazor Interactive Server, ASP.NET Core y .NET 10 |
| Persistencia | EF Core 10.0.8, SQL Server Express 2019, compatibilidad 150 |
| Extracción PDF | PdfPig 0.1.13 |
| Generación | Python 3.14, LangChain Core 1.7.0, langchain-ollama 1.1.0 |
| Inferencia | Ollama local, GGUF, `qwen2.5-coder:7b` |
| Observabilidad | C# + tabla SQL + panel Razor, sin servidor adicional |
| Publicación | DLL .NET + worker; entorno virtual externo preparado por equipo |

Las versiones declaradas en los manifiestos prevalecen si cambian posteriormente.
No incorporar paquetes Node para compilar CSS, ejecutar el modelo ni mostrar métricas.

## 3. Responsabilidades y archivos

| Componente | Responsabilidad |
|---|---|
| [Home](../../../Components/Pages/Home.razor) | Rutas, shell, perfil y apertura autorizada del visor |
| [AgentChat](../../../Components/Modules/AgentChat.razor) | Editor, mensajes, borrador, fuentes, conexión y actualización |
| [LocalAgent/LocalSearch](../../../Services/LocalSearch.cs) | Validación, selección de ruta, recuperación y reautorización |
| [Permissions](../../../Services/Permissions.cs) | Matriz de acceso y contexto del circuito |
| [DocumentService](../../../Services/DocumentService.cs) | Operaciones y auditoría autorizadas, transacciones |
| [OllamaClient](../../../Services/OllamaClient.cs) | Endpoint/modelo, contexto acotado, citas y uso de tokens |
| [LangChainRunner](../../../Services/LangChainRunner.cs) | Proceso Python, stdin/stdout, timeout y limpieza del proceso |
| [Worker](../../../LangChain/worker.py) | Validación Pydantic, plantilla, ChatOllama y salida estructurada |
| [AgentObservability](../../../Services/AgentObservability.cs) | Persistir metadatos y restringir lectura |
| [Panel](../../../Components/Modules/Observability.razor) | Últimas 500 trazas y agregados |
| [WorkspaceState](../../../Services/WorkspaceState.cs) | Estado ocupado, errores, notificaciones y refresco |

## 4. Secuencia técnica de una consulta

1. La UI captura una copia de la pregunta, la añade a mensajes y vacía el editor
   antes de esperar. El texto nuevo del usuario no se limpia al finalizar.
2. `LocalAgent` crea una traza anónima y comienza a medir.
3. Valida la pregunta y obtiene perfil activo y catálogo autorizado desde SQL.
4. Registra un evento de auditoría con descripción fija, sin texto de pregunta.
5. Recupera fuentes y contexto del último expediente por reglas.
6. Si es consulta exacta, no hay fuentes o la generación está desactivada, utiliza
   la respuesta extractiva. No solicita al LLM que cuente un contexto truncado.
7. Para generación verifica endpoint y modelo, deduplica y limita fuentes.
8. Si LangChain está seleccionado, verifica el worker y ejecuta Python como proceso
   hijo aislado, sin shell, con entorno heredado reducido y UTF-8.
9. El worker valida de nuevo endpoint/modelo; compone
   `ChatPromptTemplate | ChatOllama.with_structured_output(...)`.
10. C# valida texto, IDs de citas y contadores. Las referencias apuntan solo a
    objetos de fuente que el servidor ya suministró.
11. Relee el catálogo: bloquea si cambió el actor o alguna fuente recuperada dejó
    de estar disponible para el perfil.
12. En `finally` persiste duración, estado y metadatos de uso disponibles.
13. La UI añade respuesta y fuentes o muestra el error; refresca el estado tras éxito.
    Abrir una fuente vuelve a comprobar permisos.

## 5. Persistencia y cambios de esquema

Migraciones existentes:

1. `20261024160407_InitialSqlServer`: tablas documentales, usuarios, departamentos y auditoría.
2. `20261024174625_AgentObservability`: tabla e índice de trazas; cambio aditivo.

Usar EF Core para nuevas migraciones, revisar el SQL y respaldar antes de aplicar.
No recrear la base principal ni usarla como base desechable de pruebas.
Detalles de relaciones, nulabilidad y contratos: [data-model.md](data-model.md).

## 6. Fronteras de confianza

- **UI → C#:** no confiar en filtros o controles deshabilitados como autorización.
- **C# → Python:** solo contexto acotado por stdin; proceso sin credenciales de
  proveedores ni acceso delegado a SQL. No se envían originales ni historial completo.
- **Python → Ollama:** loopback, sin proxies heredados ni redirecciones; comprobar
  metadatos locales antes de transmitir fragmentos al modelo.
- **LLM → C#:** texto no confiable y JSON validado; mostrar con escape Razor.
- **Agente → trazas:** lista cerrada de campos, sin texto ni identidad.
- **Servicio de trazas → UI:** revalidar perfil activo y `CanAudit`.

Ollama sigue siendo un servicio local de confianza: la configuración de la app no
equivale a un firewall ni a una certificación de todo el equipo.

## 7. Construcción por fases

1. Modelo y servicios documentales autorizados.
2. Extracción PDF y recuperación léxica con referencias.
3. Agente determinista y chat con seguimiento por código.
4. Cliente Ollama con límites, validación y errores explícitos.
5. Worker LangChain, entorno virtual externo y contrato JSON.
6. UI de conexión, fuente/página, editor inmediato y refresco del catálogo.
7. Observabilidad de metadatos, migración y autorización del panel.
8. Verificación automatizada, publicación y prueba real de navegador.

Estas fases ya están implementadas. No representan trabajo nuevo pendiente.

## 8. Estrategia de verificación y operación

- Usar la suite ejecutable .NET existente; HTTP simulado para casos negativos.
- Probar Python con `unittest`, sin agregar un framework de pruebas nuevo.
- Ejecutar integración SQL solo en una base cuyo nombre termine en `_tests`.
- Reservar `--langchain` para generar realmente con una fuente sintética local.
- Verificar en navegador envío, editor vacío durante espera, borrador conservado,
  seguimiento, citas, carga visible, panel y roles restringidos.
- Publicar desde .NET; ejecutar la DLL desde su directorio de publicación.
- Confirmar `/health` y después **Comprobar conexión**: el primero no verifica inferencia.
- Tras cambios solo documentales no hace falta publicar ni reiniciar la app.

Comandos completos y resolución de problemas: [quickstart.md](quickstart.md).
