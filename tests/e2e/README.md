# Pruebas E2E de DocuSearch

Suite Playwright para el panel de agentes, aprobaciones y RBAC.

## Requisitos

- App levantada en `http://127.0.0.1:5068`.
- OpenCode/DeepSeek disponible para el resumen.
- `Assistant:UseLangGraph=true` en el entorno de la app.
- Chrome instalado (la suite usa `channel: "chrome"`).
- Una solicitud de aprobación pendiente para `approvals.spec.js`.
- Variables de entorno:
  - `E2E_EMAIL`: usuario de prueba.
  - `E2E_TOTP_SECRET`: secreto TOTP del usuario.
  - `E2E_AUDITOR_EMAIL` / `E2E_AUDITOR_TOTP_SECRET`: auditor para aprobaciones.
  - `E2E_LOW_EMAIL` / `E2E_LOW_TOTP_SECRET`: usuario `consultor` para RBAC.
  - `E2E_BASE_URL` (opcional): por defecto `http://127.0.0.1:5068`.

No se guardan credenciales ni secretos en el repositorio.

## Instalación y ejecución

```powershell
cd tests/e2e
npm install
$env:E2E_EMAIL = "usuario@example.test"
$env:E2E_TOTP_SECRET = "<secreto-totp>"
$env:E2E_AUDITOR_EMAIL = "auditor@example.test"
$env:E2E_AUDITOR_TOTP_SECRET = "<secreto-auditor>"
npx playwright test
```

Modo visible:

```powershell
npx playwright test --headed
```

Los fallos dejan captura, traza y contexto en `test-results/`.

## Casos cubiertos

1. **Resumen con LangGraph**
   - El botón muestra `Ejecutando agentes…` y la clase `running`.
   - La respuesta termina con **3 agentes**.
   - Se encienden **3 luces** en la cabecera.
   - Las tres luces tienen **colores distintos**.
   - El modal muestra **3 tarjetas activas** de Recuperador, Redactor y Verificador.
   - El botón de cierre oculta el modal.

2. **Consulta normal**
   - Termina con **1 agente**.
   - Solo se enciende **una luz**.

3. **Aprobaciones**
   - El solicitante ve su solicitud pendiente y **Cancelar solicitud**.
   - No ve aprobar su propia solicitud.
   - El auditor ve **Aprobar y ejecutar** y **Rechazar**.

4. **Rol bajo**
   - El `consultor` puede iniciar sesión.
   - El asistente no devuelve el expediente `Confidencial` ni su código en las fuentes.

## Resultado actual

```text
agent-lights.spec.js: 2 passed
approvals.spec.js:    2 passed
low-role.spec.js:     1 passed
```

Nota: el login tiene rate limit por IP. Si se repiten suites en pocos minutos, espera antes de reintentar.
