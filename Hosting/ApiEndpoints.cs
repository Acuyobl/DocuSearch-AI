using DocuSearch.Services.Mcp;
using DocuSearch.Services.Rag;
using DocuSearch.Services;

namespace DocuSearch.Hosting;

/// <summary>API HTTP del asistente, RAG y MCP.</summary>
public static class ApiEndpoints
{
    /// <summary>/assistant/engines, /api/rag/query, /mcp y /api/mcp/tokens.</summary>
    public static void MapDocuSearchApi(this WebApplication app)
    {
        // Catálogo del selector de motores: lo lee el panel del asistente y sirve para
        // comprobar desde fuera que la lista gratis/local se descubrió en este OpenCode.
        app.MapGet("/assistant/engines", async (AssistantRuntime runtime, AgentResilience resilience) =>
        {    await runtime.EnsureLoadedAsync();
            return Results.Ok(new
            {
                active = runtime.Active.Id,
                loadedAt = runtime.LoadedAt,
                error = runtime.LastError,
                circuit = resilience.Describe("opencode.generate"),
                groups = runtime.Profiles.GroupBy(profile => profile.Group).Select(group => new
                {
                    group = group.Key,
                    models = group.Select(profile => new
                    {
                        id = profile.Id, engine = profile.Engine, provider = profile.ProviderId,
                        model = profile.ModelId, available = profile.Available, label = profile.Label
                    })
                })
            });
        }).RequireSession();

        app.MapPost("/api/rag/query", async (RagQueryService rag, RagQueryRequest request) =>
        {
            try
            {
                return Results.Ok(await rag.QueryAsync(request.Question));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireSession();

        // MCP HTTP compartido con OpenCode/Claude/Cursor. El middleware anterior exige bearer.
        app.MapMcp("/mcp").RequireRateLimiting("mcp");
        // Tokens MCP: solo administración activa (la política relee el usuario en la base en cada petición).
        var mcpTokens = app.MapGroup("/api/mcp/tokens").RequireSession(AccessPolicies.Admin);
        mcpTokens.MapGet("", (LocalSession session, McpTokenService tokens) => tokens.ListAsync(session.UserId));
        mcpTokens.MapPost("", async (LocalSession session, McpTokenService tokens, McpTokenCreateRequest request) =>
        {
            var (raw, token) = await tokens.CreateAsync(request.UserId ?? session.UserId!, request.Name, request.ValidDays,
                actorName: session.DisplayName);
            return Results.Ok(new { token = raw, access = token, warning = "Guarda el token: no se vuelve a mostrar." });
        });
        mcpTokens.MapDelete("/{id}", async (LocalSession session, string id, McpTokenService tokens) =>
            await tokens.RevokeAsync(id, actorName: session.DisplayName) ? Results.NoContent() : Results.NotFound());
    }
}
