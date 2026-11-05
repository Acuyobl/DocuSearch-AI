# Presentación · DocuSearch AI

Presentación web tipo PowerPoint sobre **DocuSearch AI**, creada a partir del prompt maestro
de presentaciones. Archivo principal: [`presentacion.html`](presentacion.html).
Incluye una exportación lista para compartir: [`presentacion.pdf`](presentacion.pdf).

**Equipo:** Allan Flores Valverde · Huberth Aguilar Fallas · Carlos Hernández Molina.

## Cómo abrirla

1. Haz doble clic en `presentacion.html` (se abre en Chrome o Edge, sin instalar nada).
2. También puedes arrastrarlo a una pestaña del navegador.

> Requiere conexión a internet: Reveal.js, Chart.js y Google Fonts se cargan desde CDN.
> Sin red, la presentación no se inicializa.

## Atajos de teclado y navegación

| Acción | Cómo |
|---|---|
| Siguiente / anterior | `→` `←` `Espacio` `Av Pág` / `Re Pág`, botones laterales, clic o swipe |
| Pantalla completa | `F` o el botón de la barra superior |
| Notas del ponente | `S` (abre la ventana de notas) o el botón de notas |
| Exportar a PDF | Botón de descarga o `Ctrl+P` |
| Ayuda | `?` |
| Salir de pantalla completa / ayuda | `Esc` |

Cada diapositiva tiene **notas del ponente** con 2–4 frases sobre cómo explicarla.

## Demo interactiva (diapositiva 10)

La demo **funciona dentro de la presentación**, sin depender de la app:

- Los cinco pasos (Acceso TOTP, Panel ejecutivo, Catálogo, Asistente con citas, Auditoría) se
  pueden pulsar uno a uno.
- **«Reproducir demo»** recorre los cinco pasos automáticamente.
- El paso del asistente anima la escritura y muestra la respuesta con su **cita validada**.
- El panel ejecutivo anima sus contadores y barras; la auditoría muestra movimientos reales
  típicos (consultas, cargas, descargas, login y una alerta de integridad).
- **«Abrir la app real ↗»** abre `http://localhost:5068` para la demo en vivo si la app está
  levantada.

## Exportar a PDF

1. Pulsa el botón **PDF** de la barra superior (o `Ctrl+P`).
2. En el diálogo de impresión elige:
   - Destino: **Guardar como PDF**
   - Orientación: **Horizontal**
   - Márgenes: **Ninguno**
   - Activa **Gráficos de fondo**
3. Guarda el archivo.

La exportación ya se verificó: **11 páginas, 960×540 pt (16:9), una diapositiva por página**.
El archivo [`presentacion.pdf`](presentacion.pdf) es ese resultado.

## Móvil

La presentación mantiene el formato 16:9 y se escala al ancho del dispositivo. En teléfono,
gíralo a **horizontal** o usa el zoom del navegador. Los botones, el contador, la demo
interactiva y la navegación por gestos funcionan en pantallas pequeñas.

## Cómo personalizarla

- **Textos:** edita el HTML entre las etiquetas `<section class="slide">` (cada sección es una
  diapositiva). Las notas del ponente están en `<aside class="notes">`.
- **Colores y tipografía:** cambia las variables al principio del `<style>` (`:root`):
  `--violet`, `--cyan`, `--bg`, `--font-display`, `--font-body`…
- **Diapositivas:** duplica o elimina bloques `<section class="slide">`; el contador y la barra
  de progreso se recalculan solos.
- **Demo real:** cada captura está en `.real-shot` con `data-shot="1..13"` y los botones en
  `.real-step` con `data-step="1..13"`. La narración y los tiempos están en el arreglo `details`/`plan` de `initDemo()`.
- **Gráfico:** los datos están en `data: [87, 3, 12]` dentro de `initChart()`.
- **Imágenes:** las capturas reales están **embebidas en el HTML** (copia fuente en `presentacion-assets/`).
  Para actualizarlas, vuelve a capturar la app y reemplaza los PNG con el mismo nombre.

## Estructura (11 diapositivas)

1. Portada: propuesta, stack, equipo y cifras reales.
2. Problema y propuesta (antes/después).
3. Arquitectura en cuatro capas (diagrama esquemático).
4. Flujo principal del PDF a la respuesta citada (evidencia del endpoint `/health`).
5. El agente: el modelo propone, el sistema verifica (contexto, citas, motores).
6. El agente: motores y catálogo dinámico (OpenCode 2, LangChain activo y extractivo; «Actualizar motores»).
7. El agente: resiliencia, fallback y observabilidad local.
8. Seguridad y calidad: TOTP, RBAC, acceso local, SHA-256 y bitácora.
9. Resultados: 87 pruebas base, 102 con SQL Server y OpenCode real, 10 módulos e integración continua; gráfico real.
10. Demo real: 13 capturas de la app en ejecución, recorrido completo por los módulos, dos pasos del agente y reproducción automática de 67 s.
11. Equipo y cierre: integrantes, aprendizajes, próximos pasos y Q&A.

> El prompt maestro sugería 8 diapositivas; se amplió a 11 para incluir el detalle del agente,
> el equipo y la demo interactiva solicitados.

## Datos que faltan y marcadores `[COMPLETAR]`

- **Institución o evento:** aparece como `[COMPLETAR: institución / evento]` en la portada.
- **Duración:** 12 minutos (estimación para 11 diapositivas; ajústala en la portada).
- **Público:** mixto (técnico y de negocio).
- **Capturas:** reales, tomadas de la app en ejecución el 05/11/2026; no son maquetas.
  La demo y la portada muestran datos de esa instalación (9 expedientes, 89 páginas, 3 usuarios).
- **Logo:** wordmark textual «DocuSearch AI» con marca geométrica.
- **Métricas:** 87/102/10 están tomadas del repositorio y de la suite del proyecto.

## Verificación realizada

- 11 diapositivas exactas, sin desbordamiento a 1600×900, 1366×768 y 1920×1080.
- Reveal.js 5, plugin de notas, Chart.js y Google Fonts desde CDN sin errores de JavaScript
  (Edge puede mostrar avisos de «Tracking Prevention» sobre almacenamiento de CDN; no afectan).
- Navegación comprobada con teclado (espacio/flechas), clic y botones.
- **Recorrido real comprobado:** **13 capturas** (todos los módulos, agente y administración),
  **13 imágenes cargadas**, sin desbordamiento ni errores JS, reproducción automática de **01:07**
  con barra de progreso y parada final; enlace real a `http://localhost:5068`.
- Notas del ponente presentes en las 11 diapositivas.
- PDF de 11 páginas, horizontal, con gráfico, demo y cierre renderizados.
- **App comprobada:** 10 rutas autenticadas (dashboard, documentos, pipeline, buscador, asistente,
  reportes, auditoría, observabilidad, administración y arquitectura) y sesión TOTP real.
- **Asistente probado en la interfaz:** pregunta enviada, respuesta generada y fuentes citadas
  con el motor **LangChain · OpenCode 2** (`deepseek/deepseek-flash`).
- **Worker LangChain:** 6/6 pruebas Python y prueba C# real `--langchain-opencode` contra
  OpenCode 2, sin Ollama.
- **Suite real:** 87/87 sin servicios y 102/102 con SQL Server + OpenCode real.
- **Cifras y controles alineados con la app:** OpenCode 2 con LangChain activo y extractivo;
  «Actualizar motores» como control real y demo Docker sin la restricción de loopback
  (indicado en la diapositiva de seguridad).


