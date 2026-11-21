using System.ComponentModel;
using System.Data.Common;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using DocuSearch.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DocuSearch.Services.Mcp;

/// <summary>
/// Herramientas MCP de solo lectura. Todas usan el snapshot filtrado por RBAC de
/// DocumentService; ninguna recibe identidad, rol ni departamento como parámetro.
/// </summary>
[McpServerToolType]
public sealed class ReadOnlyDocumentTools(
    McpIdentity identity,
    DocumentService documents,
    LocalSession session,
    IDbContextFactory<AppDbContext> factory,
    ILogger<ReadOnlyDocumentTools> logger)
{
    private const int MaxResults = 20;
    private const int MaxPageChars = 20_000;
    private const int MaxSnippetChars = 500;

    [McpServerTool(Name = "list_documents"), Description(
        "Lista los expedientes que el usuario MCP puede leer. Devuelve metadatos; no incluye el texto completo.")]
    public async Task<string> ListDocuments(int limit = 20, CancellationToken cancellationToken = default)
    {
        return await GuardedAsync("list_documents", async () =>
        {
            var snapshot = await documents.LoadAsync();
            var result = snapshot.Documents
                .Take(Math.Clamp(limit, 1, MaxResults))
                .Select(document => new
                {
                    document.InternalCode,
                    document.Title,
                    document.Department,
                    document.Confidentiality,
                    document.PageCount,
                    document.Category
                });
            return Json(result);
        }, cancellationToken);
    }

    [McpServerTool(Name = "search_documents"), Description(
        "Busca en los expedientes autorizados y devuelve fragmentos con documento y página. No usa el modelo generativo.")]
    public async Task<string> SearchDocuments(string query, int limit = 8, CancellationToken cancellationToken = default)
    {
        return await GuardedAsync("search_documents", async () =>
        {
            if (string.IsNullOrWhiteSpace(query) || query.Length > 2000)
                throw new ArgumentException("La consulta debe tener entre 1 y 2000 caracteres.");
            var snapshot = await documents.LoadAsync();
            var hits = LocalSearch.Search(snapshot.Documents, query, exact: false, minimum: 1)
                .Take(Math.Clamp(limit, 1, MaxResults))
                .Select(hit => new
                {
                    hit.Document.InternalCode,
                    hit.Document.Title,
                    hit.PageNumber,
                    Snippet = Limit(hit.Snippet, MaxSnippetChars),
                    hit.Score
                });
            return Json(hits);
        }, cancellationToken);
    }

    [McpServerTool(Name = "get_document_page"), Description(
        "Devuelve el texto de una página de un expediente autorizado. No revela documentos fuera del rol.")]
    public async Task<string> GetDocumentPage(string documentCode, int page,
        CancellationToken cancellationToken = default)
    {
        return await GuardedAsync("get_document_page", async () =>
        {
            if (string.IsNullOrWhiteSpace(documentCode) || page < 1 || page > 1000)
                throw new ArgumentException("documentCode y page son obligatorios y válidos.");
            var snapshot = await documents.LoadAsync();
            var document = snapshot.Documents.FirstOrDefault(candidate =>
                candidate.InternalCode.Equals(documentCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (document is null || !Permissions.CanRead(snapshot.Actor, document))
                throw new UnauthorizedAccessException("No autorizado.");
            var documentPage = document.Pages.FirstOrDefault(candidate => candidate.PageNumber == page)
                ?? throw new ArgumentException("La página no existe.");
            return Json(new
            {
                document.InternalCode,
                document.Title,
                documentPage.PageNumber,
                Text = Limit(documentPage.ExtractedText, MaxPageChars)
            });
        }, cancellationToken);
    }

    [McpServerTool(Name = "rag_query"), Description(
        "Recupera fuentes autorizadas y devuelve una respuesta extractiva con citas. No llama al modelo generativo.")]
    public async Task<string> RagQuery(string question, CancellationToken cancellationToken = default)
    {
        return await GuardedAsync("rag_query", async () =>
        {
            if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
                throw new ArgumentException("La pregunta debe tener entre 1 y 2000 caracteres.");
            var snapshot = await documents.LoadAsync();
            var answer = LocalAgent.AnswerFromSources(question, snapshot.Documents, session);
            return Json(new
            {
                answer.Text,
                Sources = answer.Sources.Select(source => new
                {
                    source.Document.InternalCode,
                    source.Document.Title,
                    source.PageNumber,
                    Snippet = Limit(source.Snippet, MaxSnippetChars),
                    source.Score
                })
            });
        }, cancellationToken);
    }

    private async Task<string> GuardedAsync(string tool, Func<Task<string>> action, CancellationToken cancellationToken)
    {
        await identity.EnsureSignedInAsync(cancellationToken);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var status = "EXITOSO";
        try
        {
            return await action();
        }
        catch (UnauthorizedAccessException)
        {
            status = "DENEGADO";
            return Json(new { error = "not_authorized", message = "No autorizado para este recurso." });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            status = "ERROR";
            logger.LogWarning("MCP {Tool}: {Error}", tool, ex.GetType().Name);
            return Json(new { error = "invalid_request", message = ex.Message });
        }
        finally
        {
            try
            {
                timer.Stop();
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                db.Audit.Add(new AuditEvent
                {
                    UserName = session.DisplayName,
                    UserRole = session.Role,
                    Action = "MCP_TOOL",
                    Details = $"{tool}: {status}; {timer.ElapsedMilliseconds} ms",
                    DocumentCode = "SYSTEM",
                    Status = status
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is DbException or DbUpdateException)
            {
                logger.LogWarning("MCP {Tool}: no se pudo auditar ({Error}).", tool, ex.GetType().Name);
            }
        }
    }

    private static string Limit(string value, int maximum)
    {
        var truncated = TextAnalysis.Truncate(value, maximum);
        return truncated.Length <= maximum ? truncated : truncated[..maximum];
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    });
}
