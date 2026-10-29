# Asistente documental local

Implementación: Blazor/C# para permisos y recuperación, Python/LangChain para
componer la generación y Ollama local como modelo. Sin Node.js, npm ni proveedores
de IA externos. Orquestación: [LocalSearch.cs](../Services/LocalSearch.cs).

El asistente busca, lista documentos, muestra metadatos y páginas, resume y extrae
entidades. Conserva el código del último expediente dentro del circuito Blazor para
referencias como «resume ese expediente».

Las consultas exactas conservan el motor extractivo. Las demás pueden generar
respuestas con LangChain y `qwen2.5-coder:7b`, utilizando solo fragmentos autorizados.
El panel de observabilidad propio guarda metadatos en SQL Server, sin preguntas,
respuestas ni identidad. No es una instalación de Langfuse.

No llama a proveedores externos, no ejecuta herramientas arbitrarias y no requiere
credenciales de IA. Usa `DocumentService` para obtener exclusivamente documentos
permitidos por el usuario autenticado (cookie de sesión) y registrar consultas en SQL Server.

Las respuestas contienen fuentes estructuradas que abren el visor en la página citada.
No se interpreta texto de documentos como instrucciones ni como HTML.

Configuración y pruebas: [README principal](../README.md).
Diseño vigente: [Arquitectura del asistente](diseno/01-arquitectura-agente.md).

## Specs del agente

Línea base a 2026-10-24, con requisitos, contratos, decisiones, operación y trazabilidad:

| Documento | Contenido |
|---|---|
| [spec.md](specs/001-agente-documental/spec.md) | Alcance, historias, requisitos y criterios de aceptación |
| [plan.md](specs/001-agente-documental/plan.md) | Arquitectura, componentes, flujo y fases técnicas |
| [data-model.md](specs/001-agente-documental/data-model.md) | Persistencia, sesión y contratos C#/Python |
| [research.md](specs/001-agente-documental/research.md) | Decisiones y alternativas, incluido descarte de Langfuse/Node |
| [quickstart.md](specs/001-agente-documental/quickstart.md) | Instalación, configuración, pruebas y publicación |
| [tasks.md](specs/001-agente-documental/tasks.md) | Implementado, evidencia, trazabilidad y pendientes propuestos |
