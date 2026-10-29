# Estado de implementación — DocuSearch AI

Actualizado: 29 de octubre de 2026.

## Arquitectura vigente

Blazor Interactive Server + ASP.NET Core 10 + EF Core + SQL Server Express 2019.
Instancia de referencia: `ICEPALFLOR\SQLEXPRESS`, base `DocuSearch`, autenticación de
Windows para la base. El acceso web usa autenticación por cookie de ASP.NET Core:
`GET /login`, `POST /account/login` y `GET /account/logout`; la identidad procede de los
claims (`NameIdentifier`, `Name`, `Email`, `Role` y departamento), `LocalSession` se
rellena con ellos y el RBAC se aplica en servidor.
La interfaz y los permisos se implementan en C#. La generación utiliza un worker
Python aislado con LangChain y Ollama local. No hay Node.js ni compilación basada
en JavaScript del servidor.

## Alcance implementado

Dashboard, gestión documental, carga individual/masiva, pipeline, buscador,
asistente local, visor, notas, reportes, auditoría, observabilidad local del agente,
autenticación por cookie, RBAC y departamentos.
Tema claro/oscuro y diseño de panel ejecutivo conservados como referencia visual.

Los cinco expedientes de ejemplo conservan sus metadatos, diez páginas de referencia,
entidades, dos notas y contadores. Sus originales PDF no estaban incluidos en los datos.

La persistencia está implementada exclusivamente para SQL Server, incluidos los originales.
No hay fallback a almacenamiento del navegador ni a una base en memoria.
Se incluye migración de esquema y recuperación de datos anteriores desde el navegador.

## Diferencias intencionales

- RBAC aplicado también a catálogo, búsqueda, visor y reportes.
- La autenticación web es real (cookie de ASP.NET Core), pero las credenciales de
  demostración viven en `Auth:Users`. El acceso sigue limitado al equipo local por
  `Demo:LocalOnly`; para producción hacen falta ASP.NET Core Identity u OIDC y TLS.
- Texto PDF y SHA-256 reales; imágenes sin texto se marcan como Requiere OCR.
- Ingesta con resumen extractivo, entidades por reglas y relevancia por términos.
  El asistente añade generación con Ollama local y citas verificadas contra las fuentes
  autorizadas enviadas. Listas, fichas, entidades y páginas conservan respuestas exactas.
  No se afirman embeddings ni búsqueda vectorial.
- Notas compartidas persistentes con actualización explícita.
- La bitácora se conserva al restablecer los ejemplos.

## Endurecimiento P0 (rama fix/p0-hardening)

Resumen de los cambios de esta rama:

1. **Autenticación real en dos pasos (TOTP):** cookie de ASP.NET Core con formulario en
   `GET /login`, `POST /account/login` (`email`, `code` de 6 dígitos, `returnUrl` y token
   antiforgery) y `GET /account/logout`. `LocalSession` se rellena desde los claims; el
   selector de perfiles se retiró de la cabecera, que ahora muestra el usuario
   autenticado y **Salir**. `GET /enroll` muestra el QR y la clave manual de cada usuario
   para darlo de alta en Google Authenticator, Microsoft Authenticator, Authy o similar.
   Los códigos son de un solo uso (se rechaza reutilizar el mismo paso de 30 s) y la
   ventana de tolerancia es configurable (`Auth:TotpWindow`). Hay tres usuarios
   autorizados en `Auth:Users` (`acuyobl@gmail.com`, `carloshm7@gmail.com` y
   `hubertaf@gmail.com`) con su secreto Base32 y su rol.
2. **Endurecimiento:** cabeceras `X-Content-Type-Options`, `X-Frame-Options`,
   `Referrer-Policy`, `Permissions-Policy` y `Cross-Origin-Opener-Policy`, más el
   middleware `Demo:LocalOnly`, que rechaza peticiones no loopback y con cabeceras
   `X-Forwarded-*`. Al publicar: `Demo:LocalOnly=false` y TLS (`Security:Https:Enabled`).
3. **Integridad:** al servir un PDF se recalcula el SHA-256 y se compara con el
   registrado; una discrepancia cancela la entrega y audita `INTEGRITY_MISMATCH`
   (estado ADVERTENCIA). Los ejemplos sin original muestran **Sin original**.
4. **Concurrencia:** `ViewAsync` y `FileAsync` ya no toman `sp_getapplock`; el lock queda
   para las mutaciones y el parseo PDF del reproceso ocurre fuera de la transacción y del
   lock. Los contadores se actualizan con `ExecuteUpdateAsync`.
5. **Confirmación destructiva:** «Restablecer datos de ejemplo» exige escribir
   `RESTABLECER` en la interfaz y el servidor valida ese texto en `ResetAsync`.
6. **Interfaz:** `ErrorBoundary` por módulo con **Reintentar**, cierre con `Escape` y foco
   inicial en diálogos, `WorkspaceState` captura cualquier excepción (salvo cancelación)
   para no tumbar el circuito y las barras de cuota usan cultura invariante.
7. **Repositorio:** licencia MIT, `DocuSearch.sln`, `.editorconfig`, `.gitattributes`,
   `SECURITY.md`, `CONTRIBUTING.md` y CI en `.github/workflows/ci.yml` (build y 76
   comprobaciones). Se retiraron artefactos (`package-lock.json` e
   `IA de generacion .txt`) y los prompts pasaron a UTF-8.
8. **Asistente con OpenCode:** motor seleccionable por configuración que usa el servidor
   local de OpenCode (`POST /session` y `POST /session/:id/message`) con un agente sin
   herramientas (`zodiac-asistente`), validación de citas y borrado de la sesión. Mantiene
   LangChain/Ollama y el modo extractivo como alternativas; los modelos local (`ollama/qwen3:4b`)
   y remoto (`deepseek/deepseek-flash`) se eligen por configuración y la interfaz avisa
   cuando el modelo es remoto. Añade 8 comprobaciones (7 simuladas y 2 reales con `--opencode`).

Pendientes que no cubre esta rama: embeddings/OCR, autenticación de producción con
Identity u OIDC, un lock global para la importación y accesibilidad completa con trampa
de foco.

## Validación y pendientes de entorno

Se dispone de compilación .NET y suite ejecutable de comprobaciones sin base,
incluidos PDF reales, límites exactos, permisos y respuestas del asistente con citas.
La base `DocuSearch` fue creada e inicializada en la instancia confirmada.
La suite dispone de 90 comprobaciones: 76 sin servicios (incluidos los vectores RFC 6238
del algoritmo TOTP), tres de LangChain real y once de integración en la base aislada `DocuSearch_tests`:
inicialización idempotente, PDF persistente,
metadatos, notas, reproceso, permisos, cuotas, asistente, importación, concurrencia y rollback;
se verifican el envío exclusivo de fuentes autorizadas y la revocación durante la generación.
La observabilidad propia persiste metadatos en SQL Server, sin contenido ni identidad,
con acceso de administración general/auditoría. No usa Langfuse ni Node.js.
Se verificaron los nueve módulos, el agente con fuente por página, RBAC y exportación CSV
en el navegador. La integración continua ejecuta `dotnet build` y las 76 comprobaciones
sin servicios.

Antes de uso en red faltan sustituir los secretos TOTP y roles de configuración por
ASP.NET Core Identity u OIDC con TOTP/passkeys, TLS y políticas de despliegue.
OCR de imágenes y búsqueda semántica con embeddings son ampliaciones no implementadas.

Consulta [README](README.md) para instalar, configurar, ejecutar y verificar.
