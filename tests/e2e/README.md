# Pruebas E2E del panel de agentes

Suite Playwright para el indicador visual del Asistente IA.

## Requisitos

- App levantada en `http://127.0.0.1:5068`.
- OpenCode/DeepSeek disponible para el resumen.
- `Assistant:UseLangGraph=true` en el entorno de la app.
- Chrome instalado (la suite usa `channel: "chrome"`).
- Variables de entorno:
  - `E2E_EMAIL`: usuario de prueba.
  - `E2E_TOTP_SECRET`: secreto TOTP del usuario.
  - `E2E_BASE_URL` (opcional): por defecto `http://127.0.0.1:5068`.

No se guardan credenciales ni secretos en el repositorio.

## Instalación y ejecución

```powershell
cd tests/e2e
npm install
$env:E2E_EMAIL = "usuario@example.test"
$env:E2E_TOTP_SECRET = "<secreto-totp>"
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

## Resultado actual

```text
Running 2 tests using 1 worker
ok  resumen activa 3 agentes, colores distintos y modal visible
ok  una consulta normal muestra un solo agente
2 passed
```
