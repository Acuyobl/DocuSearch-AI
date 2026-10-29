# Tareas y trazabilidad del agente

**Referencia:** [AGENTE-001](spec.md) · **Corte:** 2026-10-24.

`[x]` indica implementación existente y evidencia disponible al documentar.
`[ ]` indica propuesta o validación futura: no se anuncia como funcionalidad activa.
Este documento no obliga a ejecutar los pendientes sin una solicitud posterior.

## 1. Base y persistencia

- [x] **T01 — Migrar a .NET:** aplicación Blazor/ASP.NET Core sin compilación o runtime Node.
- [x] **T02 — Configurar SQL Server:** instancia local, EF Core, compatibilidad 150,
  autenticación Windows y migración inicial.
- [x] **T03 — Persistir documentos:** metadatos, PDF original, páginas, entidades y notas.
- [x] **T04 — Aplicar RBAC en servidor:** lectura, escritura, descarga, administración y auditoría.
- [x] **T05 — Ingestar PDF real:** límites, texto PdfPig, SHA-256, cuotas y transacción por archivo.
- [x] **T06 — Mostrar resultado de carga:** selección visible, progreso, fallo y confirmación.

Dependencias: T03 depende de T02; T04 depende de T02; T05 depende de T03/T04;
T06 depende de T05. T01 habilita la aplicación que integra estas capacidades.

## 2. Recuperación y experiencia de chat

- [x] **T07 — Recuperar fuentes:** coincidencia exacta y relevancia por términos sobre catálogo autorizado.
- [x] **T08 — Resolver consultas exactas:** listas, fichas, páginas, entidades y último código.
- [x] **T09 — Construir chat:** mensajes, sugerencias, citas y limpieza de conversación.
- [x] **T10 — Ajustar envío:** vaciar editor al enviar y preservar borrador posterior.
- [x] **T11 — Abrir fuentes:** documento/página mediante visor con nueva autorización.
- [x] **T12 — Actualizar catálogo:** carga inicial de catálogo/asistente y botón de refresco.

Dependencias: T07 depende de T03/T04; T08 depende de T07; T09 depende de T08;
T10 depende de T09; T11 depende de T04/T09; T12 depende de T03/T09.

## 3. Generación local y LangChain

- [x] **T13 — Validar proveedor local:** endpoint loopback y metadatos GGUF/completion sin atributos remotos.
- [x] **T14 — Acotar contexto y citas:** seis fuentes, límite de fragmentos y IDs del servidor.
- [x] **T15 — Preparar Python:** entorno virtual externo y dependencias declaradas, sin Node.
- [x] **T16 — Componer LangChain:** plantilla + ChatOllama + salida estructurada.
- [x] **T17 — Integrar proceso:** stdin/stdout UTF-8, sin shell, timeout y limpieza.
- [x] **T18 — Manejar fallos:** sin fallback silencioso y sin fuentes inventadas.
- [x] **T19 — Reautorizar:** bloquear resultado si cambió el perfil/acceso durante generación.
- [x] **T20 — Publicar worker:** incluir script, mantener Python/entorno virtual fuera de publicación.

Dependencias: T14 depende de T07/T13; T16 depende de T14/T15; T17 depende de T16;
T18 depende de T17; T19 depende de T04/T17; T20 depende de T17.

## 4. Observabilidad privada

- [x] **T21 — Definir metadatos:** tabla sin contenido documental ni identidad.
- [x] **T22 — Migrar trazas:** migración aditiva `AgentObservability`, sin resetear documentos.
- [x] **T23 — Propagar uso real:** contadores del motor, nulos cuando no están disponibles.
- [x] **T24 — Instrumentar agente:** duración, estado y categoría incluso ante fallo de consulta.
- [x] **T25 — Omitir nuevas preguntas de auditoría:** evento fijo de `AGENT_QUERY`.
- [x] **T26 — Crear panel:** últimas 500, media/p95, estados y tokens; actualización manual.
- [x] **T27 — Restringir panel:** administración general y auditoría en UI y servicio.
- [x] **T28 — Informar fallo de telemetría:** error explícito al no persistir.

Dependencias: T22 depende de T02/T21; T23 depende de T16/T17; T24 depende de T08/T22/T23;
T25 depende de T08; T26 depende de T24; T27 depende de T04/T26; T28 depende de T24.

## 5. Validación y documentación

- [x] **T29 — Pruebas .NET:** permisos, búsqueda, PDF, límites, contratos, errores y privacidad.
- [x] **T30 — Pruebas Python:** validación local, composición, parser, citas, tracing,
  UTF-8 y tokens desde metadatos.
- [x] **T31 — Integración SQL aislada:** persistencia, permisos, rollback, concurrencia y trazas.
- [x] **T32 — Inferencia real:** C# → Python/LangChain → Ollama con fuente sintética y cita.
- [x] **T33 — Navegador:** envío/editor, seguimiento, fuente/página, documento cargado,
  consulta con traza real, roles de observabilidad y móvil sin desbordamiento global.
- [x] **T34 — Compilar/publicar:** release, migraciones consistentes, worker presente y salud SQL.
- [x] **T35 — Crear paquete Specs:** estos seis documentos y referencias desde documentación del agente.

Dependencias: T29/T30 dependen de los componentes que ejercitan; T31 depende de T22/T29;
T32 depende de T17/T23; T33 depende de T10/T11/T12/T26/T27/T34;
T34 depende de T20/T22 y validación automatizada; T35 depende del estado documentado.

### Evidencia de corte, no promesa de resultados futuros

| Verificación | Resultado registrado |
|---|---|
| `dotnet run --project .\tests\DocuSearch.Checks.csproj -- --database --langchain` | 79 correctas, 0 fallidas |
| `python -m unittest discover -s .\LangChain -p "test_*.py" -v` en el entorno virtual | 8 correctas |
| Build Release | 0 errores y 0 advertencias |
| Migraciones | Sin cambios de modelo pendientes al cierre de la integración |
| Navegador: inferencia y observabilidad | Traza local real con duración y contadores de entrada/salida |
| Navegador: autorización | Consultor sin filas; auditor y administración general con acceso |
| Navegador: vista móvil | 390 px sin desbordamiento horizontal global en el panel comprobado |

Las pruebas SQL restablecen exclusivamente la base de pruebas. Los documentos
del usuario no se utilizan como fixtures destructivos. Crear este paquete es un
cambio documental; no implica una nueva ejecución de todas las pruebas anteriores.

## 6. Matriz requisito → implementación → evidencia

| Requisito | Tareas | Evidencia principal |
|---|---|---|
| RF-01 Ingesta | T03 a T06, T12 | Pruebas PDF/SQL y flujo de selección/guardado en navegador |
| RF-02 Chat | T09/T10 | Editor vacío mientras está ocupado y borrador conservado |
| RF-03 Fuentes | T11/T14/T18 | Validación de citas y apertura de página en visor |
| RF-04 Motor apropiado | T07/T08/T16 | Pruebas extractivas y generación real |
| RF-05 Permisos | T04/T19 | Contexto del consultor y revocación durante generación |
| RF-06 Localidad | T13/T15/T17/T18 | Rechazo de endpoint/modelos remotos y sin fallback |
| RF-07 Observabilidad | T21 a T28 | Esquema privado, persistencia, tokens y acceso restringido |
| RNF-01/RNF-02 | T01/T13/T17 | Middleware loopback y ejecución .NET/Python |
| RNF-03/RNF-04 | T05/T06/T18/T28 | Estados honestos y errores explícitos |
| RNF-05 | T03/T22/T31 | SQL persistente y migraciones |
| RNF-06 | T09/T10/T33 | Tema, responsive y estados de UI |
| RNF-07 | T21/T23/T25/T27/T29/T30 | Lista de campos, auditoría nueva sin pregunta y tracing apagado |

Fuentes de pruebas: [.NET](../../../tests/Program.cs) y
[Python](../../../LangChain/test_worker.py).

## 7. Pendientes propuestos, sujetos a aprobación

- [ ] **P01 — Autenticación real:** identidad web y políticas por usuario antes de uso en red.
- [ ] **P02 — Despliegue endurecido:** TLS, permisos de servicio, respaldo/restauración y
  política de operación. Depende de P01 para exposición multiusuario.
- [ ] **P03 — Retención de trazas:** plazo, cuota y borrado aprobado; no borrar eventos
  históricos ni contenido del usuario sin una decisión explícita.
- [ ] **P04 — Tiempos por etapa:** separar recuperación, arranque Python e inferencia;
  mantener privacidad y evitar registrar prompts.
- [ ] **P05 — Evaluación de respuestas:** conjunto sintético en español, calidad de
  citas y resistencia a instrucciones dentro de documentos.
- [ ] **P06 — Búsqueda semántica:** embeddings/modelos locales, índice y pruebas RBAC,
  solo si se aprueba una ampliación de arquitectura.
- [ ] **P07 — OCR de imágenes:** motor local, costo de procesamiento y límites medidos.
- [ ] **P08 — Memoria conversacional:** alcance, borrado, autorización y privacidad;
  no enviar historial automáticamente.
- [ ] **P09 — Cancelación de consulta:** propagación de cancelación al worker y estado
  de traza distinguible de timeout.
- [ ] **P10 — Sincronización entre pestañas:** notificaciones o refresco controlado,
  sin recurrir a recargas que borren conversaciones.

No reintroducir Langfuse/Node.js ni OpenCode como parte de estos pendientes sin
una modificación explícita de restricciones y una especificación nueva.
