# Cambios de producción aplicados — 13/11/2026

**Alcance:** endurecimiento que depende solo del código. No sustituye IdP, SQL Server Standard, TLS, respaldos ni observabilidad; esos siguen en [`DE-DEMO-A-PRODUCCION.md`](DE-DEMO-A-PRODUCCION.md).

**Verificación realizada (13/11/2026):**

- Compilación correcta de `DocuSearch.csproj` y `tests/DocuSearch.Checks.csproj` en una copia de trabajo (0 errores, 0 advertencias).
- **87/87 comprobaciones base** ejecutadas y correctas desde una copia limpia del binario; **97/97** con `--verifier` (10 checks de AG-02), **92/92** con `--inspector` (5 de AG-10), **92/92** con `--catalog` (5 de AG-01), **92/92** con `--audit` (5 de AG-05) y **92/92** con `--auth` (5 de bloqueo de login). El entorno de trabajo bloquea la ejecución directa por Control de aplicaciones; la copia elimina esa marca.
- Verificación HTTP en vivo: `/login` 200 con `Cache-Control: no-store`, `X-Frame-Options`, CORP y X-Permitted; `/enroll` 404 sin secretos; `/health/live` 200; `/health/ready` 503 sin base; `/` redirige a `/login`.
- Rate limiting comprobado: 10 peticiones permitidas y después **429 con cuerpo JSON** `{"error":"rate_limited"}`.
- `ProductionGuard` comprobado con `Demo:LocalOnly=false`: rechaza el arranque y enumera TLS, reset, seed, `TrustServerCertificate` y `AllowedHosts`.
- **No se ejecutaron** las variantes `--database`, `--database --langchain` ni `--langchain-opencode` (requieren SQL Server/OpenCode reales).

En la máquina del proyecto:

```powershell
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --verifier
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --inspector
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --catalog
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --audit
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --auth
dotnet run --project .\tests\DocuSearch.Checks.csproj -c Debug -- --database --langchain
```

Debe seguir dando **87/87** y **102/102** (y 88 con `--langchain-opencode` si aplica).

---

## 1. Cambios aplicados

| Área | Cambio | Archivos | Efecto |
|---|---|---|---|
| Guarda de producción | Si `Demo:LocalOnly=false`, el arranque exige TLS, `/enroll` apagado, reset y seed apagados, sin TOTP inline, sin `TrustServerCertificate=True` y `AllowedHosts` real. | `Services/ProductionGuard.cs`, `Program.cs` | Impide exponer la demo con los atajos activos; falla con mensajes claros. |
| Excepción controlada | `Demo:AllowInsecureExposure=true` convierte las guardas en advertencia. | `ProductionGuard.cs`, `appsettings.json` | Permite la demo Docker sin TLS, declarando el riesgo. |
| Rate limiting | 10 intentos/minuto por IP en `/login` y `/account/login`, 429 al exceder. | `Program.cs`, `appsettings.json` | Frena fuerza bruta de TOTP y enumeración de correos. |
| Bloqueo por cuenta | Tras 10 fallos por cuenta, bloqueo temporal de 5 minutos (configurable). | `Services/LoginAttemptLimiter.cs`, `AuthService.cs`, `appsettings.json` | Añade defensa por cuenta además del límite por IP; es por instancia. |
| Logs estructurados | `Logging:Json=true` emite JSON; cada petición registra método, ruta, estado y duración, y devuelve `X-Request-Id`. | `Program.cs`, `appsettings.json` | Correlación sin registrar query string ni contenido. |
| Retención de trazas | Purga manual de `AgentTraces` anteriores a `Observability:RetentionDays` (90) por administración general; audita `PURGE_TRACES`. | `Services/AgentObservability.cs`, `Observability.razor`, `appsettings.json` | Cierra el hueco de retención de trazas; no afecta documentos ni auditoría. |
| `/enroll` | Solo responde en loopback; sigue apagado por defecto. | `Program.cs` | Ya no expone secretos a la red. |
| Auditoría | Nuevos eventos `LOGIN_FAILED` y `LOGOUT`; el login correcto ya se auditaba. | `Services/AuthService.cs`, `Program.cs` |Investigación de accesos sin guardar códigos ni contraseñas. |
| Cookies | `Secure` cuando `Security:Https:Enabled=true`. | `Program.cs` | La cookie no viaja por HTTP en producción. |
| Cabeceras | `Cross-Origin-Resource-Policy`, `X-Permitted-Cross-Domain-Policies` y `Cache-Control: no-store` en login/enroll/cuenta. | `Program.cs` | Endurecimiento adicional y sin caché de páginas sensibles. |
| Proxy/TLS | `UseForwardedHeaders` opcional con `KnownProxies`; HSTS cuando HTTPS está activo. | `Program.cs`, `appsettings.json` | Compatible con reverse proxy sin confiar en cualquier cabecera. |
| Salud | Nuevas sondas `/health/live` y `/health/ready` (esta última falla si hay migraciones pendientes). `/health` se conserva. | `Program.cs` | Listo para orquestadores/balanceadores. |
| Reset de demo | Se oculta en Administración y el servicio lo rechaza si `Demo:AllowReset=false`. | `Services/DemoPolicy.cs`, `DocumentService.cs`, `Administration.razor` | Evita borrar datos reales por accidente. |
| Seed de demo | Solo siembra si `Demo:SeedOnInitialize=true`. | `Data/DatabaseSetup.cs`, `Program.cs` | Una base nueva de producción nace vacía. |
| Plantilla de producción | Ejemplo completo con valores seguros y sin secretos. | `appsettings.Production.example.json` | Punto de partida para el despliegue real. |

## 2. Configuración

- La plantilla **no se carga automáticamente**. Copiarla como `appsettings.Production.json` en el servidor y sustituir dominio, conexión y proxies.
- Los secretos TOTP siguen fuera del repositorio (`Auth:TotpSecrets` por user-secrets o variables de entorno); para producción real se espera un IdP.
- Si se usa la **demo Docker** con `Demo:LocalOnly=false`, añadir `Demo__AllowInsecureExposure=true` y asumir que no hay TLS ni controles de producción.
- `Security:LoginRateLimitPerMinute` permite ajustar el límite; es un límite **por instancia y por IP**.
- `Security:ForwardedHeaders:KnownProxies` debe contener solo las IP del proxy; no usar `*`.
- `AllowedHosts` debe listar el dominio real (separado por `;`).

## 3. Pendiente (requiere decisiones o infraestructura)

- **IdP/OIDC** y gestión real de usuarios/roles; hoy siguen en configuración y el login los sobrescribe.
- **SQL Server Standard/Azure SQL**, respaldos, restauración probada, TDE y retención.
- **TLS/certificado y reverse proxy** reales; la plantilla solo declara la intención.
- **Observabilidad** (logs estructurados, métricas, trazas, alertas) y runbooks.
- **Antivirus/sandbox de PDF**, pen test, SAST/DAST y revisión de dependencias.
- **Revocación de sesión inmediata** y bloqueo de cuenta **distribuido** (el bloqueo actual es por instancia).
- **CSP activa**: la plantilla propone una, pero hay que probarla en navegador antes de activarla.
- **Retención/legal hold/WORM** de auditoría y trazas.
- **Alta disponibilidad** de Blazor Server (backplane, sticky sessions, claves compartidas).
- **Multiagente**: no iniciar hasta cerrar G1–G5 de `DE-DEMO-A-PRODUCCION.md`.

## 4. Pruebas manuales recomendadas

1. `dotnet build` y la suite de 87 checks.
2. Login correcto, login fallido (verificar `LOGIN_FAILED`), logout (verificar `LOGOUT`).
3. Superar 10 intentos/minuto desde la misma IP y comprobar HTTP 429.
4. Abrir `/enroll` por loopback y desde otra máquina (debe dar 404).
5. Con `Demo:AllowReset=false`, comprobar que el panel de reset desaparece y que la llamada al servicio falla.
6. `/health/live` y `/health/ready`; simular migración pendiente.
7. Con `Demo:LocalOnly=false` y `Security:Https:Enabled=false`, comprobar que el arranque falla con la lista de problemas.
8. Con `Demo:AllowInsecureExposure=true`, comprobar que solo advierte (demo Docker).
