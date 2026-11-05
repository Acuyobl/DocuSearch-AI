# DocuSearch AI — Resumen ejecutivo

**Control documental local con un agente que cita sus fuentes.**

> **En una frase:** DocuSearch AI convierte un repositorio de PDFs en un catálogo auditable y consultable. La IA es opcional, nunca decide permisos, y cada respuesta se respalda con documento y página.

**Instancia de referencia:** `http://localhost:5068` · 05/11/2026 · 11 diapositivas.

---

## 1. El problema

- La información vive dispersa en PDFs, carpetas y correos.
- Buscar dentro de un documento es lento y no siempre deja evidencia de dónde salió el dato.
- La IA generativa suele responder sin citar, sin respetar permisos y enviando información a la nube.

## 2. La propuesta

**Control local, evidencia verificable e IA opcional.**

- **Todo el control está en C#:** autenticación TOTP, RBAC, ingesta, integridad y auditoría.
- **La IA no decide permisos:** recibe solo fragmentos autorizados y devuelve citas que el servidor valida.
- **Modo extractivo siempre disponible:** si no hay motor de IA o falla, la app sigue respondiendo.
- **Local-first:** SQL Server Express, archivos locales y OpenCode en la misma máquina.

## 3. Cómo funciona

1. **Carga validada** — hasta 25 MB, 500 páginas y 20 PDF por lote.
2. **Extracción por página** — texto, resumen y entidades con reglas; sin inventar contenido.
3. **Integridad** — SHA-256 del original, metadatos y guardado transaccional en SQL Server.
4. **Búsqueda autorizada** — relevancia por términos y permisos aplicados en el servidor.
5. **Respuesta con cita** — el asistente responde en lenguaje natural y valida cada fuente.

## 4. Qué incluye

| Capacidad | Resultado |
|---|---|
| **10 módulos** | Dashboard, documentos, carga, pipeline, buscador, asistente, visor, reportes, auditoría y administración |
| **Seguridad** | TOTP, RBAC por rol/departamento, SHA-256 y bitácora persistente |
| **Asistente documental** | Preguntas, resúmenes, páginas exactas, entidades, listados y seguimiento acotado |
| **Observabilidad** | Motor, modelo, duración, tokens y error; sin guardar preguntas ni contenido |
| **Resiliencia** | 3 intentos, backoff, timeout de 75 s, circuit breaker y fallback extractivo |

## 5. El agente en 60 segundos

**Puede:**

- responder preguntas sobre los expedientes autorizados;
- resumir un documento;
- citar la página exacta;
- extraer montos, fechas, leyes, personas y organizaciones etiquetadas;
- listar y filtrar por categoría o confidencialidad;
- reconocer códigos como `CNT-2026-EEAEE0BD49F8`;
- seguir una conversación de forma **acotada** al último expediente citado.

**No puede:**

- navegar por internet, leer archivos, usar SQL o herramientas;
- hacer OCR de escaneos;
- recordar todo el historial en el modelo;
- comparar documentos completos: recibe como máximo **6 fragmentos de 1000 caracteres**;
- saltarse RBAC ni inventar citas.

**Motor actual:** **LangChain · OpenCode 2 · deepseek/deepseek-flash**, con fallback extractivo. El modelo no recibe herramientas y las citas se validan en C#.

## 6. Evidencia real

| Indicador | Valor |
|---|---|
| Expedientes en la instalación | **9** |
| Páginas procesadas | **89** |
| Departamentos / usuarios | **6 / 3** |
| Pruebas sin servicios | **87/87** |
| Pruebas con SQL Server + OpenCode | **102/102** |
| Pruebas LangChain + OpenCode real | **88/88** |
| Worker LangChain | **6/6** |
| Demo de la presentación | **13 capturas reales · 67 s** |
| PDF de la presentación | **11 páginas** |

**Respuesta real capturada:**

> **LangChain · OpenCode · deepseek/deepseek-flash**
> El nivel de servicio garantiza una disponibilidad del 99.99 % durante la vigencia, con tiempo máximo de respuesta de cuatro horas hábiles para incidentes críticos…
> **CNT-2026-EEAEE0BD49F8 · p. 2**

## 7. Tres mensajes para llevar

1. **La IA suma cuando el contexto y los permisos son del sistema, no del modelo.**
2. **Local-first es viable:** sin nube obligatoria, con integridad y auditoría demostrables.
3. **Decir lo que falta genera confianza:** OCR y búsqueda semántica son los siguientes pasos, no promesas ocultas.

## 8. Próximos pasos

- Identity/OIDC con TOTP o passkeys y TLS para publicar.
- OCR para escaneos y búsqueda híbrida con embeddings.
- Almacenamiento externo y mayor capacidad que SQL Express.

## 9. Equipo

- **Allan Flores Valverde**
- **Huberth Aguilar Fallas**
- **Carlos Hernández Molina**

**DocuSearch AI · 2026** · *Menos carpetas. Más decisiones.*

---

*Resumen basado en la presentación de 11 diapositivas y en la aplicación real en ejecución. El marcador `[COMPLETAR: institución / evento]` de la portada sigue pendiente de completar.*
