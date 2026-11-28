# DocuSearch AI — notas para Claude Code

## Orientarse con poco consumo
- Antes de leer archivos grandes, consulta `graphify-out/GRAPH_REPORT.md` (nodos centrales, comunidades) y `graphify explain "ruta::Símbolo"`.
- Si el grafo está desfasado respecto a `git rev-parse HEAD`, ejecuta `graphify update .` (solo código, sin LLM ni coste).
- Archivos más grandes: `tests/Program.cs` (2.4k líneas), `Services/DocumentService.cs`, `Services/OpenCodeClient.cs`, `Services/Rag/RagQueryService.cs`. Léelos por rangos.
- Arranque: `Program.cs` solo encadena `Hosting/` (servicios, middleware en orden, endpoints) y `Cli/CliCommands.cs` (comandos `--opción`).

## Proyecto
- Blazor Server .NET 10 + EF Core SQL Server; RAG en `Services/Rag/`; workers Python opcionales en `LangChain/`; servidor MCP en `DocuSearch.Mcp/` y `/mcp`.
- Checks: `dotnet run --project tests/DocuSearch.Checks.csproj -c Debug [-- --rag|--auth|--governance|...]`.
- Documentación y commits en español (`fix(rag): …`).
- Debilidades priorizadas y plan: `docs/GUIA-MEJORAS.md`.
