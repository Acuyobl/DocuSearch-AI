using DocuSearch.Data;
using DocuSearch.Services.Rag;
using DocuSearch.Services;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;

namespace DocuSearch.Cli;

/// <summary>Comandos de mantenimiento por línea de comandos (<c>dotnet run -- --opción</c>).</summary>
public static class CliCommands
{
    /// <summary>Ejecuta el comando pedido en <paramref name="args"/>; devuelve true si había uno y la aplicación no debe arrancar.</summary>
    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        if (args.Contains("--initialize-database", StringComparer.Ordinal))
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await DatabaseSetup.InitializeAsync(db);
                app.Logger.LogInformation("Base SQL Server inicializada. Los datos existentes se han conservado.");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                app.Logger.LogError(ex, "No se pudo inicializar SQL Server. Revisa la instancia, los permisos de Windows y ConnectionStrings:Documents.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--reindex-embeddings", StringComparer.Ordinal))
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var indexer = scope.ServiceProvider.GetRequiredService<EmbeddingIndexer>();
                var updated = await indexer.ReindexAllAsync();
                Console.WriteLine($"Índice semántico actualizado: {updated} páginas.");
                var pinecone = scope.ServiceProvider.GetService<PineconeSyncService>();
                if (pinecone is { Enabled: true })
                {
                    var synced = await pinecone.SyncAllAsync();
                    Console.WriteLine($"Pinecone: {synced} vectores sincronizados.");
                }
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
            {
                app.Logger.LogError(ex, "No se pudo reindexar la búsqueda semántica. Revisa Embeddings:BaseUrl y el modelo configurado.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--sync-pinecone", StringComparer.Ordinal))
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var pinecone = scope.ServiceProvider.GetRequiredService<PineconeSyncService>();
                if (!pinecone.Enabled)
                {
                    Console.WriteLine("Pinecone está desactivado o falta Pinecone:ApiKey.");
                    Environment.ExitCode = 1;
                    return true;
                }
                var synced = await pinecone.SyncAllAsync();
                Console.WriteLine($"Pinecone: {synced} vectores sincronizados.");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
            {
                app.Logger.LogError(ex, "No se pudo sincronizar Pinecone. Revisa Pinecone:ApiKey, IndexName y conectividad.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--rag-reindex", StringComparer.Ordinal))
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<RagIngestionService>();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var result = await ingestion.IngestAsync(new SqlRagLoader(factory));
                Console.WriteLine($"RAG: {result.Documents} documentos, {result.Chunks} fragmentos indexados.");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
            {
                app.Logger.LogError(ex, "No se pudo reindexar el pipeline RAG por fragmentos.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--rag-pinecone-sync", StringComparer.Ordinal))
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<PineconeRagVectorStore>();
                var synced = await store.SyncExistingAsync();
                Console.WriteLine($"Pinecone RAG: {synced} fragmentos sincronizados.");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException)
            {
                app.Logger.LogError(ex, "No se pudo sincronizar los fragmentos RAG con Pinecone.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--rag-eval-export", StringComparer.Ordinal))
        {
            try
            {
                var position = Array.IndexOf(args, "--rag-eval-export");
                var outputPath = position + 1 < args.Length ? args[position + 1] : "";
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    Console.WriteLine("Uso: --rag-eval-export ruta.json [--generate]");
                    Environment.ExitCode = 1;
                    return true;
                }
                await using var scope = app.Services.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var user = await db.Users.AsNoTracking()
                    .Where(candidate => candidate.Status == "Activo" && candidate.Role == "admin_general")
                    .FirstOrDefaultAsync()
                    ?? throw new InvalidOperationException("No hay administrador activo para exportar evaluación RAG.");
                var session = scope.ServiceProvider.GetRequiredService<LocalSession>();
                session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(AuthService.BuildClaims(user), "cli")));
                var evaluation = scope.ServiceProvider.GetRequiredService<RagEvaluationService>();
                var questions = RagQuestionsPath(args);
                var count = await evaluation.ExportAsync(questions, outputPath, args.Contains("--generate", StringComparer.Ordinal));
                Console.WriteLine($"RAG eval: {count} casos de {Path.GetFileName(questions)} exportados en {outputPath}.");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or InvalidDataException or JsonException)
            {
                app.Logger.LogError(ex, "No se pudo exportar el set de evaluación RAG.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--rag-eval", StringComparer.Ordinal))
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var user = await db.Users.AsNoTracking()
                    .Where(candidate => candidate.Status == "Activo" && candidate.Role == "admin_general")
                    .FirstOrDefaultAsync()
                    ?? throw new InvalidOperationException("No hay administrador activo para evaluar RAG.");
                var session = scope.ServiceProvider.GetRequiredService<LocalSession>();
                session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(AuthService.BuildClaims(user), "cli")));
                var evaluation = scope.ServiceProvider.GetRequiredService<RagEvaluationService>();
                var path = RagQuestionsPath(args);
                var report = await evaluation.RunAsync(path, args.Contains("--generate", StringComparer.Ordinal));
                Console.WriteLine($"RAG eval ({Path.GetFileName(path)}): {report.Cases} casos evaluados");
                Console.WriteLine($"Context Recall: {report.ContextRecall:F3}");
                Console.WriteLine($"Context Precision: {report.ContextPrecision:F3}");
                Console.WriteLine($"MRR: {report.MeanReciprocalRank:F3}");
                Console.WriteLine($"Faithfulness (proxy): {report.Faithfulness:F3}");
                Console.WriteLine($"Answer Relevancy (proxy): {report.AnswerRelevancy:F3}");
                Console.WriteLine($"Casos sin la fuente esperada: {report.FailedCases.Count}" +
                    (report.FailedCases.Count > 0 ? $" ({string.Join(", ", report.FailedCases)})" : ""));
                foreach (var detail in report.FailureDetails) Console.WriteLine("  " + detail);
            if (report.MissingCases.Count > 0)
                    Console.WriteLine($"Casos omitidos, su documento no existe: {string.Join(", ", report.MissingCases)}");
                if (report.LeakyCases.Count > 0)
                    Console.WriteLine($"AVISO: {report.LeakyCases.Count} preguntas revelan la respuesta; el recall está inflado.");
                if (report.ContextRecall < 0.8 || report.Faithfulness < 0.9)
                    Console.WriteLine("Umbrales del prompt no alcanzados (0.80 / 0.90).");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or IOException or InvalidDataException or JsonException)
            {
                app.Logger.LogError(ex, "No se pudo ejecutar la evaluación RAG.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--rag-ingest-pdf", StringComparer.Ordinal) || args.Contains("--rag-ingest-rest", StringComparer.Ordinal))
        {
            try
            {
                var isPdf = args.Contains("--rag-ingest-pdf", StringComparer.Ordinal);
                var position = Array.IndexOf(args, isPdf ? "--rag-ingest-pdf" : "--rag-ingest-rest");
                var sourceArgument = position + 1 < args.Length ? args[position + 1] : "";
                if (string.IsNullOrWhiteSpace(sourceArgument))
                {
                    Console.WriteLine(isPdf ? "Uso: --rag-ingest-pdf ruta.pdf" : "Uso: --rag-ingest-rest https://...");
                    Environment.ExitCode = 1;
                    return true;
                }
                await using var scope = app.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<RagSourceStore>();
                var ingestion = scope.ServiceProvider.GetRequiredService<RagIngestionService>();
                IRagLoader loader;
                HttpClient? restHttp = null;
                if (isPdf)
                {
                    loader = new PdfRagLoader(sourceArgument);
                }
                else
                {
                    restHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    loader = new RestRagLoader(restHttp, sourceArgument);
                }
                try
                {
                    var documents = await loader.LoadAsync();
                    foreach (var document in documents) await store.EnsureDocumentAsync(document);
                    // Se reutilizan los documentos ya cargados: la fuente (PDF o API REST) se lee una sola vez.
                    var result = await ingestion.IngestAsync(documents);
                    Console.WriteLine($"RAG {loader.SourceType}: {result.Documents} fuente(s), {result.Chunks} fragmentos.");
                }
                finally
                {
                    restHttp?.Dispose();
                }
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or HttpRequestException or IOException)
            {
                app.Logger.LogError(ex, "No se pudo ingerir la fuente RAG.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        if (args.Contains("--search", StringComparer.Ordinal))
        {
            try
            {
                var position = Array.IndexOf(args, "--search");
                var query = position + 1 < args.Length ? args[position + 1] : "";
                if (string.IsNullOrWhiteSpace(query))
                {
                    Console.WriteLine("Uso: --search \"tu consulta\"");
                    Environment.ExitCode = 1;
                    return true;
                }

                await using var scope = app.Services.CreateAsyncScope();
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                var user = await db.Users.AsNoTracking()
                    .Where(candidate => candidate.Status == "Activo" && (candidate.Role == "admin_general" || candidate.Role == "gestor_documental"))
                    .OrderBy(candidate => candidate.Role == "admin_general" ? 0 : 1)
                    .FirstOrDefaultAsync()
                    ?? throw new InvalidOperationException("No hay un usuario activo para la consulta de diagnóstico.");
                var session = scope.ServiceProvider.GetRequiredService<LocalSession>();
                session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(AuthService.BuildClaims(user), "cli")));

                var documents = await scope.ServiceProvider.GetRequiredService<DocumentService>().LoadAsync();
                var embeddings = scope.ServiceProvider.GetRequiredService<EmbeddingClient>();
                var queryVector = await embeddings.TryEmbedAsync(query);
                var pinecone = scope.ServiceProvider.GetRequiredService<PineconeRetriever>();
                var externalScores = queryVector is null || !pinecone.Enabled
                    ? null
                    : await pinecone.SearchAsync(queryVector, documents.Documents.Select(document => document.Id).ToArray());
                var hits = LocalSearch.Search(documents.Documents, query, minimum: 20, queryVector: queryVector,
                    externalScores: externalScores).Take(5).ToList();
                Console.WriteLine($"Consulta: {query}");
                Console.WriteLine(queryVector is null ? "Vector: no disponible (recuperación léxica)." : "Vector: Pinecone e5 (búsqueda híbrida).");
                Console.WriteLine(pinecone.Enabled ? "Pinecone: consultado." : "Pinecone: desactivado.");
                Console.WriteLine($"Resultados: {hits.Count}");
                foreach (var hit in hits)
                    Console.WriteLine($"{hit.Score,3} · {hit.Document.InternalCode} p.{hit.PageNumber} · {TextAnalysis.Truncate(hit.Snippet, 160)}");
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                app.Logger.LogError(ex, "No se pudo ejecutar la consulta de diagnóstico.");
                Environment.ExitCode = 1;
            }
            return true;
        }

        return false;
    }

    // --questions ruta.json elige otro set; por defecto eval/rag_questions.json junto al binario o en el directorio actual.
    private static string RagQuestionsPath(string[] args)
    {
        var position = Array.IndexOf(args, "--questions");
        if (position >= 0)
        {
            var custom = position + 1 < args.Length ? args[position + 1] : "";
            if (string.IsNullOrWhiteSpace(custom) || custom.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException("Uso: --questions ruta.json");
            return Path.GetFullPath(custom);
        }
        var path = Path.Combine(AppContext.BaseDirectory, "eval", "rag_questions.json");
        return File.Exists(path) ? path : Path.Combine(Directory.GetCurrentDirectory(), "eval", "rag_questions.json");
    }
}
