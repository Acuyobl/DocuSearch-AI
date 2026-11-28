# Modern Web Guidance aplicado a DocuSearch AI

**Repositorio analizado:** https://github.com/GoogleChrome/modern-web-guidance  
**Fecha:** 25/11/2026

## 1. Qué es y qué no es

`modern-web-guidance` **no es una librería que se agregue al runtime de la app**. Es un conjunto de *skills* y un CLI para que los agentes de código generen web moderna en lugar de patrones legacy.

Comandos relevantes:

```powershell
npx modern-web-guidance@latest install
npx modern-web-guidance@latest search "accessible dialog focus"
npx modern-web-guidance@latest retrieve dialog
npx modern-web-guidance@latest legacy-scan .
```

Contiene 269 guías de plataforma: CSS, HTML/DOM, JavaScript, accesibilidad, UX, rendimiento, formularios, componentes, WebMCP y Built-in AI. Es útil para **guiar el desarrollo**, no para resolver una dependencia de producción.

## 2. Resultado del escaneo real

Ejecuté:

```powershell
npx --yes modern-web-guidance@latest legacy-scan .
```

Resultado:

```text
Found 1 modernization opportunity across 1 guide
* dark-mode-toggle
  wwwroot/app.js:7
  document.documentElement.dataset.theme = theme;
```

El toggle aplica el tema con `data-theme` y `localStorage`, pero **no actualiza `<meta name="color-scheme">` ni `color-scheme`**. Por eso los controles nativos, scrollbars y `light-dark()` no siguen correctamente la preferencia.

El escaneo es estático y solo cubre reglas deterministas de parte del corpus; no sustituye una auditoría manual.

## 3. Dónde sí podemos aplicarlo

| Área de DocuSearch | Guía relacionada | Beneficio | Riesgo |
|---|---|---|---|
| Tema claro/oscuro | `dark-mode-toggle`, `dark-mode` | Correcta integración con controles nativos | Bajo |
| Panel de agentes modal | `dialog`, `light-dismiss-a-dialog`, `prevent-page-scroll-under-modal-dialog` | Foco, Escape, backdrop e `inert` correctos | Medio |
| Pestañas Auditoría | `tabs` | Navegación con teclado y ARIA correcta | Bajo |
| Tablas de documentos/auditoría | `sortable-table` | Ordenar y navegar con teclado | Medio |
| Formularios de carga/login | `field-sizing`, `:user-invalid` | Validación nativa y campos que crecen | Bajo |
| Tipografía y respuestas | `typography`, `text-wrap` | Mejor legibilidad de respuestas largas | Bajo |
| Listas y tablas largas | `content-visibility` | Menos trabajo de layout al inicio | Bajo |
| Cards y panel de agentes | `container queries`, `card` | Layout según el componente, no el viewport | Bajo |
| Sidebar móvil | `navigation-drawer` | Drawer accesible y gestos predecibles | Medio |
| Mensajes/errores | `persistent-toast-notifications` | Mensajes no bloqueantes y apilables | Medio |
| Progreso de ingesta | `progress`, `progress-ring` | Estados nativos accesibles | Bajo |
| Movimiento de luces/agentes | `motion`, `prefers-reduced-motion` | Animación eficiente y respetuosa | Bajo |

## 4. Lo que NO recomiendo ahora

### WebMCP

El repo incluye guías `webmcp`, `agentic-forms` y `agentic-javascript-tools`. Sería tentador exponer la búsqueda o el panel de agentes a agentes del navegador.

**No conviene por ahora:**

- DocuSearch ya tiene un servidor MCP backend con RBAC, tokens y auditoría.
- WebMCP es experimental y depende de soporte del navegador/host.
- Expondría acciones del cliente, incluyendo formularios sensibles.
- Duplicaría superficie de ataque y autenticación.

Mantener **MCP backend** como integración oficial.

### Built-in AI del navegador

`Translator`, `Summarizer` y `LanguageDetector` locales son interesantes, pero:

- disponibilidad y modelos varían por navegador/equipo;
- la app ya usa OpenCode/DeepSeek y tiene prompts auditados;
- añadiría otra ruta de datos y calidad difícil de medir.

Se puede evaluar en el futuro para traducir respuestas, nunca para reemplazar el RAG.

### WASM/C++

No hay necesidad: no hay cómputo intensivo en cliente que lo justifique.

## 5. Plan de implementación priorizado

### Fase 1 — Quick wins (bajo riesgo)

1. **`color-scheme` correcto**
   - Añadir `<meta name="color-scheme" content="light dark">` en `Components/App.razor`.
   - Script inline mínimo para evitar FOUC, antes de cargar `app.js`.
   - Actualizar `toggleTheme()` y `localStorage`.
   - Declarar `color-scheme` en CSS y mapear `data-theme`.
   - Referencia: `dark-mode-toggle` y `dark-mode`.

2. **Tipografía**
   - `text-wrap: balance` en títulos cortos.
   - `text-wrap: pretty` en párrafos/respuestas.
   - `text-box: trim-both` donde aplique.
   - Referencia: `typography`, `improve-text-layout-and-legibility`.

3. **Composer y formularios**
   - `field-sizing: content` progresivo en `textarea` del asistente.
   - Estilos `:user-invalid` para validación.
   - Referencia: `field-sizing`, `:user-valid`/`:user-invalid`.

4. **Listas largas**
   - `content-visibility: auto` + `contain-intrinsic-size` en tablas/listas.
   - Referencia: `content-visibility`.

### Fase 2 — Accesibilidad de componentes

5. **Diálogo nativo**
   - Sustituir el backdrop casero de `AgentChat` por `<dialog>` + `showModal()`/`close()`.
   - Interop JS pequeña; devolver foco al botón que abrió.
   - Usar `::backdrop`, `Escape` y `inert`.
   - Referencia: `dialog`, `light-dismiss-a-dialog`, `prevent-page-scroll-under-modal-dialog`.

6. **Tabs de Auditoría**
   - Añadir `aria-controls`, `id` de paneles, `tabindex` móvil y flechas/Home/End.
   - Mantener una sola pestaña activa.
   - Referencia: `tabs`.

7. **Drawer/sidebar móvil**
   - Evaluar `navigation-drawer` con foco atrapado y cierre por gesto.
   - Referencia: `navigation-drawer`.

### Fase 3 — Productividad y tablas

8. **Tablas ordenables** con `aria-sort`, encabezados botón y persistencia de orden.
9. **Toasts** para confirmaciones no bloqueantes de aprobaciones/carga.
10. **Container queries** en cards de agentes y métricas.

## 6. Cambio concreto de mayor valor inmediato

El escaneo señala este punto exacto:

```js
// wwwroot/app.js
toggleTheme() {
    const theme = document.documentElement.dataset.theme === "light" ? "dark" : "light";
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("docusearch-theme", theme);
}
```

Falta reflejar el tema en `color-scheme`. Corrección propuesta:

```html
<!-- Components/App.razor -->
<meta name="color-scheme" content="light dark" />
<script>
  try {
    const theme = localStorage.getItem("docusearch-theme");
    if (theme === "dark" || theme === "light")
      document.querySelector('meta[name="color-scheme"]').content = theme;
  } catch {}
</script>
```

```css
:root { color-scheme: light dark; }
:root[data-theme="dark"] { color-scheme: dark; }
:root[data-theme="light"] { color-scheme: light; }
```

Esto afecta a scrollbars, `input`, `select`, `textarea`, diálogos nativos y `light-dark()` sin cambiar el diseño actual.

## 7. Instalación como skill para futuros agentes

No es necesario en runtime, pero sí útil en el repositorio:

```powershell
npx modern-web-guidance@latest install
```

Es un asistente interactivo: elegir el agente/editor que use el equipo. Instala documentación, no un paquete de producción.

Uso recomendado en el flujo de desarrollo:

```powershell
npx modern-web-guidance@latest search "accessible dialog focus trap"
npx modern-web-guidance@latest retrieve dialog
npx modern-web-guidance@latest legacy-scan .
```

## 8. Pruebas para cada cambio

- **Visual**: Playwright existente (`tests/e2e`) y capturas antes/después.
- **Teclado**: Tab, Shift+Tab, Escape, flechas en tabs, foco devuelto en dialog.
- **Preferencias**: `prefers-color-scheme`, `prefers-reduced-motion`, `forced-colors`.
- **Rendimiento**: Lighthouse/INP antes/después; no afirmar mejoras sin medir.
- **Seguridad**: ningún cambio debe relajar RBAC, MCP ni autenticación.
- **Regresión**: `legacy-scan` y suites C#/E2E.

## 9. Conclusión

Sí hay valor, pero **no como dependencia de la app**. El mayor retorno está en:

1. `color-scheme` y tema;
2. diálogo nativo accesible para el panel de agentes;
3. tabs con teclado;
4. formularios y tipografía moderna;
5. rendimiento de listas largas.

WebMCP y Built-in AI deben esperar: la app ya tiene MCP backend y un pipeline RAG auditado; añadirlos ahora aumentaría riesgo sin beneficio claro.


## 10. Estado aplicado (Fase 1)

- [x] `<meta name="color-scheme">` + sincronización en `toggleTheme()`.
- [x] Script inline mínimo para evitar FOUC del tema.
- [x] `color-scheme` coherente con `data-theme`.
- [x] `text-wrap: balance` en títulos y `pretty` en respuestas.
- [x] `:user-invalid` para validación nativa.
- [x] `field-sizing: content` en el composer.
- [x] `scrollbar-color`/`scrollbar-width` progresivos.
- [x] `content-visibility` progresivo en filas de auditoría y repositorio.
- [x] Prueba E2E `tests/e2e/theme.spec.js` (pasa).

Fases 2 y 3 quedan pendientes: `<dialog>` nativo, tabs con teclado, drawer, tabla ordenable, toasts y container queries.


## 11. Estado aplicado (Fase 2)

- [x] Panel de agentes migrado a `<dialog>` nativo con `showModal()`.
- [x] `::backdrop`, cierre con botón/Escape y devolución de foco.
- [x] `aria-expanded`/`aria-haspopup` sincronizados.
- [x] Pestañas de Auditoría con `aria-controls`, `tabpanel`, `tabindex` y navegación Arrow/Home/End.
- [x] Pruebas E2E actualizadas: `agent-lights` 2/2 y regresión de tabs.
- [ ] Drawer móvil, tabla ordenable y toasts: fases siguientes.


## 12. Estado aplicado (Fase 3 parcial)

- [x] Tabla de documentos ordenable con `aria-sort`, botones de encabezado y flecha de dirección.
- [x] Container query en el panel de agentes para distribuir tarjetas según el ancho del componente.
- [x] E2E de ordenación de tabla.
- [ ] Toasts persistentes y drawer móvil: se dejan para una fase posterior por no ser críticos.

La app queda operativa; no se modificó RBAC, MCP ni el pipeline RAG en esta fase.
