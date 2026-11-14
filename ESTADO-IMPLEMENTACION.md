# Estado de implementación — DocuSearch AI

**Fecha:** 13/11/2026 · **Base:** código actual del repositorio
**Documentos relacionados:** [`DE-DEMO-A-PRODUCCION.md`](DE-DEMO-A-PRODUCCION.md) (hoja de ruta) · [`MULTIAGENTES-ANALISIS.md`](MULTIAGENTES-ANALISIS.md) (catálogo y decisiones) · [`PRODUCCION-CAMBIOS.md`](PRODUCCION-CAMBIOS.md) (detalle de los cambios de producción).

---

## 1. Lo implementado

### 1.1 Producción y seguridad

| Área | Implementado | Evidencia |
|---|---|---|
| Guarda de exposición | `ProductionGuard` impide arrancar con `Demo:LocalOnly=false` si faltan TLS, reset/seed apagados, `/enroll` cerrado, sin TOTP inline, sin `TrustServerCertificate=True` y con `AllowedHosts` real. Salida explícita para demo Docker. | `Services/ProductionGuard.cs`, `Program.cs` |
| Login | TOTP de un solo uso, rate limit (10/min por IP), bloqueo por cuenta (10 fallos/5 min), auditoría de `LOGIN_FAILED`/`LOGOUT`, `/enroll` solo loopback, cookie `Secure` con HTTPS. | `Program.cs`, `Services/AuthService.cs`, `Services/LoginAttemptLimiter.cs` |
| Cabeceras y proxy | HSTS, CSP configurable, CORP, X-Permitted, `no-store` en autenticación, `X-Request-Id`; forwarded headers opcionales con proxies declarados. | `Program.cs`, `appsettings.json` |
| Salud | `/health`, `/health/live`, `/health/ready` (falla con migraciones pendientes). | `Program.cs` |
| Observabilidad | Logs JSON opcionales, correlación por petición, trazas del agente sin contenido, purga de trazas antiguas por administración. | `Program.cs`, `Services/AgentObservability.cs`, `Observability.razor` |
| Demo | Reset oculto/bloqueado y seed desactivable; plantilla de producción sin secretos. | `Services/DemoPolicy.cs`, `DocumentService.cs`, `appsettings.Production.example.json` |

### 1.2 Agentes y multiagente

| Agente | Estado | Qué hace | Prueba |
|---|---|---|---|
| **AG-02 Verificador** | Implementado | Revisa longitud, citas 1..N, fuente autorizada, página existente, par duplicado y texto en página/resumen antes de mostrar una respuesta generada; fallback extractivo. Integrado en OpenCode directo, LangChain y Ollama. | `--verifier` |
| **AG-01 Catalogador** | MVP opt-in | Sugerencia manual en el formulario de metadatos: una llamada, consentimiento explícito, propuesta sin escritura, confidenciales bloqueados con motor remoto, validación AG-02. | `--catalog` |
| **AG-05 Auditor** | Implementado | Resumen determinista de riesgos en la bitácora: fallos de login, accesos denegados, integridad, cambios administrativos, descargas y acciones frecuentes. | `--audit` |
| **AG-10 Inspector** | Implementado | Informe de calidad en `/quality`: OCR pendiente, originales ausentes, metadatos incompletos, duplicados SHA-256 e integridad verificable. | `--inspector` |
| **AG-04 Redactor** | Ya existía | Asistente documental actual; no se reimplementó. | `--verifier` |

**No implementado aún:** cola persistente (`AgentJobs`/`AgentSteps`), worker en segundo plano, aprobaciones formales, sugerencias asíncronas masivas y presupuesto por tarea. AG-01 funciona solo de forma manual y síncrona.

### 1.3 Pruebas y CI

- CI ejecuta compilación, 87 checks base y las suites `--verifier`, `--inspector`, `--catalog`, `--audit` y `--auth`.
- Runner local: [`tests/run-all.ps1`](tests/run-all.ps1).
- Suite base: 87 comprobaciones sin servicios.
- Suites nuevas: 10 (AG-02), 5 (AG-10), 5 (AG-01), 5 (AG-05), 5 (bloqueo de login).

---

## 2. Lo que hace falta

### 2.1 Gates de producción

| Gate | Falta | Impacto |
|---|---|---|
| **Identidad** | IdP/OIDC real (Entra ID u otro), gestión de usuarios fuera de `appsettings`, recuperación de cuenta, revocación inmediata de sesión, bloqueo distribuido. | Hoy la identidad es TOTP de configuración; el login sincroniza y sobrescribe usuarios. |
| **Datos** | SQL Server Standard/Azure SQL, respaldos + restauración probada, TDE, retención/legal hold, auditoría WORM/SIEM, capacidad. | SQL Server Express tiene techo de 10 GB; los PDF viven en la base y no hay respaldo operativo. |
| **Perímetro** | Certificado TLS real, reverse proxy, dominio, activación y validación de CSP, WAF/rate limit de borde. | La configuración ya lo soporta, pero falta la infraestructura. |
| **Operación** | APM/métricas/alertas, entornos separados, CI/CD de despliegue, gate de migraciones, pruebas de carga, runbooks, HA de Blazor Server. | Los logs JSON y las sondas existen; falta la plataforma de observabilidad y el despliegue. |
| **Seguridad/cumplimiento** | SAST/DAST, pen test, antivirus/sandbox de PDF, aviso de privacidad, derechos de datos, revisión de accesos, respuesta a incidentes. | El código no sustituye estos controles organizativos. |
| **Gobierno de IA** | Contrato/DPA del proveedor, residencia de datos, cuotas y presupuesto por usuario, evaluación con golden set, versionado de prompts, kill switch. | AG-01 ya exige consentimiento y bloquea confidenciales con motor remoto, pero no hay DPA ni cuotas. |

### 2.2 Plataforma multiagente

- Tablas `AgentJobs`, `AgentSteps` y `AgentSuggestions`, migración y retención.
- `BackgroundService` con lease, reintentos, idempotencia y `dead-letter`.
- Identidad del solicitante propagada y revalidada en cada paso.
- Aprobaciones formales para aplicar sugerencias; hoy se aplican manualmente desde el formulario.
- Presupuesto por tarea y observabilidad por paso.
- Decisión pendiente sobre AG-03 Recuperador, AG-06 Observador, AG-07 Archivista, AG-08 Migrador y AG-09 Guía.

### 2.3 Pruebas pendientes de ejecutar en un entorno real

- `--database --langchain`: 102 comprobaciones con SQL Server real y LangChain/Ollama (requiere Ollama).
- `--langchain-opencode`: 88 comprobaciones con LangChain + OpenCode (requiere servidor OpenCode).
- `--opencode`: integración contra un servidor OpenCode real.
- Prueba end-to-end de AG-01 con un modelo real (el parser y la integración están cubiertos, no la llamada real).
- Prueba de la purga de trazas contra SQL real (la suite SQL ya cubre el resto de operaciones).

---

## 3. Pruebas ejecutadas (13/11/2026)

Entorno: sin OpenCode en `:4097` (el asistente queda en modo extractivo); SQL Server disponible en el contenedor `ds-sql` y su proxy en `127.0.0.1:14331`.

| Suite | Comando | Resultado |
|---|---|---|
| Base | `dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug` | **87/87** |
| AG-02 Verificador | `... -- --verifier` | **97/97** |
| AG-10 Inspector | `... -- --inspector` | **92/92** |
| AG-01 Catalogador | `... -- --catalog` | **92/92** |
| AG-05 Auditor | `... -- --audit` | **92/92** |
| Bloqueo de login | `... -- --auth` | **92/92** |
| SQL Server real | `... -- --database` (base `DocuSearch_tests`) | **99/99** |
| Compilación | `dotnet build` | **0 errores, 0 advertencias** |
| Runtime | login TOTP, `/`, `/quality`, `/observability`, `/health`, `/health/ready` | 200 |

**No ejecutado:** `--database --langchain` (requiere Ollama), `--langchain-opencode` (requiere OpenCode) y la generación real del Catalogador. Ver [`GUIA-PRUEBAS.md`](GUIA-PRUEBAS.md).

---

## 4. Orden recomendado

1. **Ejecutar integración real** con SQL Server y OpenCode (`--database --langchain`, `--langchain-opencode`).
2. **Cerrar gates**: IdP/OIDC, SQL Server Standard con respaldos/TDE, TLS/proxy, APM/alertas.
3. **Plataforma multiagente** (cola/aprobaciones) solo después de los gates.
4. **Gobierno de IA**: DPA, cuotas, evaluación y kill switch antes de usar AG-01 con documentos reales.

---

## 5. Archivos clave

- Agentes: [`Services/AgentVerifier.cs`](Services/AgentVerifier.cs), [`Services/CatalogSuggester.cs`](Services/CatalogSuggester.cs), [`Services/DocumentInspector.cs`](Services/DocumentInspector.cs), [`Services/AuditInspector.cs`](Services/AuditInspector.cs).
- Producción: [`Services/ProductionGuard.cs`](Services/ProductionGuard.cs), [`Services/LoginAttemptLimiter.cs`](Services/LoginAttemptLimiter.cs), [`Services/DemoPolicy.cs`](Services/DemoPolicy.cs).
- UI: [`Components/Modules/Quality.razor`](Components/Modules/Quality.razor), [`Components/Modules/MetadataDialog.razor`](Components/Modules/MetadataDialog.razor), [`Components/Modules/Audit.razor`](Components/Modules/Audit.razor), [`Components/Modules/Observability.razor`](Components/Modules/Observability.razor).
- Configuración: [`appsettings.json`](appsettings.json), [`appsettings.Production.example.json`](appsettings.Production.example.json).
- Pruebas: [`tests/Program.cs`](tests/Program.cs), [`tests/run-all.ps1`](tests/run-all.ps1), [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
