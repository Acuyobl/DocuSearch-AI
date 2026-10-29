# Estado de implementación — DocuSearch AI

Actualizado: 24 de octubre de 2026.

## Arquitectura vigente

Blazor Interactive Server + ASP.NET Core 10 + EF Core + SQL Server Express 2019.
Instancia configurada: `ICEPALFLOR\SQLEXPRESS`, base `DocuSearch`, autenticación de Windows.
La interfaz y los permisos se implementan en C#. La generación utiliza un worker
Python aislado con LangChain y Ollama local. No hay Node.js ni compilación basada
en JavaScript del servidor.

## Alcance implementado

Dashboard, gestión documental, carga individual/masiva, pipeline, buscador,
asistente local, visor, notas, reportes, auditoría, observabilidad local del agente,
perfiles RBAC y departamentos.
Tema claro/oscuro y diseño de panel ejecutivo conservados como referencia visual.

Los cinco expedientes de ejemplo conservan sus metadatos, diez páginas de referencia,
entidades, dos notas y contadores. Sus originales PDF no estaban incluidos en los datos.

La persistencia está implementada exclusivamente para SQL Server, incluidos los originales.
No hay fallback a almacenamiento del navegador ni a una base en memoria.
Se incluye migración de esquema y recuperación de datos anteriores desde el navegador.

## Diferencias intencionales

- RBAC aplicado también a catálogo, búsqueda, visor y reportes.
- El acceso al servidor está limitado al equipo local porque el selector de perfiles
  sigue siendo una demostración, no autenticación.
- Texto PDF y SHA-256 reales; imágenes sin texto se marcan como Requiere OCR.
- Ingesta con resumen extractivo, entidades por reglas y relevancia por términos.
  El asistente añade generación con Ollama local y citas verificadas contra las fuentes
  autorizadas enviadas. Listas, fichas, entidades y páginas conservan respuestas exactas.
  No se afirman embeddings ni búsqueda vectorial.
- Notas compartidas persistentes con actualización explícita.
- La bitácora se conserva al restablecer los ejemplos.

## Validación y pendientes de entorno

Se dispone de compilación .NET y suite ejecutable de comprobaciones sin base,
incluidos PDF reales, límites exactos, permisos y respuestas del asistente con citas.
La base `DocuSearch` fue creada e inicializada en la instancia confirmada.
La suite dispone de 79 comprobaciones: 65 sin servicios, tres de LangChain real
y once de integración en la base aislada `DocuSearch_tests`:
inicialización idempotente, PDF persistente,
metadatos, notas, reproceso, permisos, cuotas, asistente, importación, concurrencia y rollback;
se verifican el envío exclusivo de fuentes autorizadas y la revocación durante la generación.
La observabilidad propia persiste metadatos en SQL Server, sin contenido ni identidad,
con acceso de administración general/auditoría. No usa Langfuse ni Node.js.
Se verificaron los nueve módulos, el agente con fuente por página, RBAC y exportación CSV
en el navegador.

Antes de uso en red faltan autenticación real, TLS y políticas de despliegue.
OCR de imágenes y búsqueda semántica con embeddings son ampliaciones no implementadas.

Consulta [README](README.md) para instalar, configurar, ejecutar y verificar.
