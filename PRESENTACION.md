# DocuSearch AI — Control documental local

Plataforma de gestión de expedientes PDF con Blazor, ASP.NET Core y SQL Server Express.

## Flujo

1. Seleccionar uno o varios PDF y sus metadatos.
2. Validar formato, tamaño y cantidad de páginas; calcular SHA-256.
3. Extraer texto por página con PdfPig.
4. Elaborar resumen extractivo y entidades por reglas.
5. Guardar metadatos y originales en SQL Server dentro de una transacción.
6. Consultar con búsqueda contextual, coincidencia exacta o asistente local con citas.

## Módulos

- Dashboard y métricas.
- Catálogo con filtros y edición.
- Carga y reproceso.
- Buscador y asistente documental.
- Visor PDF, texto, resumen, entidades, notas y ficha técnica.
- Reportes CSV, informe HTML e impresión a PDF.
- Auditoría persistente.
- Observabilidad local: duración, modelo, tokens y categorías de error, sin contenido.
- Administración de perfiles, departamentos y cuotas.
- Arquitectura y recuperación de datos anteriores.

## Privacidad y alcance

El asistente conecta ASP.NET Core con un worker Python de LangChain y Ollama local
con `qwen2.5-coder:7b`, sin Node.js, npm, OpenCode ni servicios de IA externos.
Genera respuestas a partir de fragmentos autorizados y valida las referencias contra
las fuentes enviadas. Las listas, fichas, entidades y páginas explícitas conservan
su respuesta exacta por reglas. La IA puede equivocarse; se deben revisar las citas.
Los escaneos sin texto requieren un motor OCR adicional.
No hay embeddings ni métricas de similitud vectorial ficticias.

El acceso usa inicio de sesión real con cookie de ASP.NET Core (`/login`) y **verificación
en dos pasos TOTP**: la clave es el código temporal de 6 dígitos de la app autenticadora.
Ya no hay selector anónimo de perfiles ni contraseña estática. Tres usuarios autorizados
(`acuyobl@gmail.com`, `carloshm7@gmail.com` y `hubertaf@gmail.com`) permiten comprobar los
permisos aplicados en servidor, con RBAC resuelto a partir de los claims; el alta se hace
desde `GET /enroll` con un QR. Esos secretos viven en configuración y deben sustituirse
por ASP.NET Core Identity u OIDC con TOTP/passkeys antes de publicar, junto con TLS.
Por defecto el acceso sigue limitado al equipo local (`Demo:LocalOnly`).
La conexión utiliza autenticación de Windows, instancia `ICEPALFLOR\SQLEXPRESS`
y base `DocuSearch`, ya inicializada. Detalles en [README](README.md).
