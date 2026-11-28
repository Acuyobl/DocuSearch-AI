using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using DocuSearch.Services;
using DocuSearch.Services.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;

var passed = 0;
var failed = 0;
void Assert(bool condition, string message = "Condición incumplida")
{
    if (!condition) throw new InvalidOperationException(message);
}
async Task Check(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}\n{ex}"); }
}
Task Sync(Action action) { action(); return Task.CompletedTask; }
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Se esperaba {typeof(T).Name}.");
}
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Se esperaba {typeof(T).Name}.");
}
// La sesión ya no trae un usuario por defecto: el sujeto se establece desde los claims,
// igual que en la aplicación web tras el inicio de sesión.
LocalSession Session(string userId = "usr-1")
{
    var session = new LocalSession();
    session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, userId),
        new Claim(ClaimTypes.Name, "Checks"),
        new Claim(ClaimTypes.Role, "admin_general")
    ], "checks")));
    return session;
}

var seed = DatabaseSetup.ReadSeed();
var admin = seed.Users.Single(u => u.Role == "admin_general");
var manager = seed.Users.Single(u => u.Role == "gestor_documental");
var auditor = seed.Users.Single(u => u.Role == "auditor");
// Solo hay tres cuentas configuradas; los roles sin cuenta se prueban con fixtures
// en memoria para cubrir la matriz RBAC completa sin sembrar usuarios de más.
var consultant = new AppUser
{
    Id = "chk-consultor", Name = "Consultor de prueba", Email = "consultor.checks@example.test",
    Role = "consultor", Department = "Recursos Humanos", Status = "Activo"
};
var deptAdmin = new AppUser
{
    Id = "chk-admin-depto", Name = "Admin de departamento de prueba", Email = "depto.checks@example.test",
    Role = "admin_depto", Department = "Legal & Contratos", Status = "Activo"
};
var secret = seed.Documents.Single(d => d.Confidentiality == "Estrictamente Secreto");
var contract = seed.Documents.Single(d => d.InternalCode == "CNT-2025-0892");
var policy = seed.Documents.Single(d => d.InternalCode == "POL-2025-0012");
var pdf = PdfFixture.Create("Cloud contract SLA 99.99%. Annual price $450000 USD.");
var processor = new PdfProcessor();
OpenCodeClient OpenCodeFor(HttpClient http, bool enabled = true, string password = "secreto", string provider = "deepseek", string model = "deepseek-flash", string agent = "zodiac-asistente") =>
    new(http, Options.Create(new OpenCodeOptions
    {
        Enabled = enabled, BaseUrl = "http://127.0.0.1:4097", Password = password,
        Agent = agent, ProviderId = provider, ModelId = model
    }));
HttpClient OpenCodeMock(string answerText, Action<HttpRequestMessage>? capture = null) =>
    new(new TestHttpHandler((request, _) =>
    {
        capture?.Invoke(request);
        if (request.Method == HttpMethod.Delete) return Task.FromResult(JsonResponse("true"));
        var path = request.RequestUri!.AbsolutePath;
        var body = (request.Method.Method, path) switch
        {
            (_, "/api/info") => """{"version":"2.2.5"}""",
            (_, "/api/agent") => """{"data":[{"id":"build"},{"id":"zodiac-asistente"}]}""",
            ("POST", "/api/session") => """{"data":{"id":"ses_test"}}""",
            ("POST", "/api/session/ses_test/prompt") => """{"data":{}}""",
            (_, "/api/session/ses_test/message") =>
                "{\"data\":[{\"type\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(answerText) +
                "}],\"tokens\":{\"input\":10,\"output\":5},\"finish\":\"stop\"}]}",
            _ => "{}"
        };
        return Task.FromResult(JsonResponse(body));
    }));
HttpClient OpenCodeCatalogMock(string providerJson, string modelJson) =>
    new(new TestHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = path switch
        {
            "/api/info" => """{"version":"2.2.5"}""",
            "/api/agent" => """{"data":[{"id":"build"}]}""",
            "/api/provider" => providerJson,
            "/api/model" => modelJson,
            _ => "{}"
        };
        return Task.FromResult(JsonResponse(body));
    }));
const string catalogProvidersJson = """
    {"data":[
      {"id":"opencode","name":"OpenCode","settings":{"baseURL":""}},
      {"id":"deepseek","name":"DeepSeek","settings":{"baseURL":"https://api.deepseek.com"}}
    ]}
    """;
const string catalogModelsJson = """
    {"data":[
      {"id":"free-model","modelID":"free-model","providerID":"opencode","name":"Free Model","cost":[{"input":0,"output":0}],"status":"active","enabled":true},
      {"id":"paid-model","modelID":"paid-model","providerID":"opencode","name":"Paid Model","cost":[{"input":1,"output":2}],"status":"active","enabled":true},
      {"id":"deepseek-flash","modelID":"deepseek-flash","providerID":"deepseek","name":"DeepSeek Flash","cost":[{"input":0,"output":0}],"status":"active","enabled":true}
    ]}
    """;
HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

await Check("Cinco expedientes, tres perfiles y seis departamentos preservados", () => Sync(() =>
    Assert(seed.Documents.Count == 5 && seed.Users.Count == 3 && seed.Departments.Count == 6)));
await Check("Los perfiles del seed coinciden con las cuentas TOTP configuradas", () => Sync(() =>
{
    var emails = seed.Users.Select(u => u.Email).OrderBy(e => e, StringComparer.Ordinal).ToArray();
    Assert(emails.SequenceEqual(new[]
    {
        "admin.demo@example.test", "auditor.demo@example.test", "gestor.demo@example.test"
    }));
    Assert(seed.Users.All(u => u.Status == "Activo"));
}));
await Check("Páginas y notas de ejemplo preservadas", () => Sync(() =>
    Assert(seed.Documents.Sum(d => d.PageCount) == 124 && seed.Documents.Sum(d => d.Pages.Count) == 10 && seed.Documents.Sum(d => d.Notes.Count) == 2)));
await Check("Pages y Notes se deserializan una vez y se invalidan al cambiar el JSON", () => Sync(() =>
{
    var item = new DocumentItem();
    item.Pages = [new DocumentPage(1, "uno", "", [])];
    var first = item.Pages;
    Assert(ReferenceEquals(first, item.Pages), "Lecturas repetidas deben reutilizar la lista.");
    item.Pages = [new DocumentPage(1, "dos", "", [])];
    Assert(item.Pages[0].ExtractedText == "dos", "El setter debe invalidar la caché.");
    item.PagesJson = """[{"pageNumber":3,"extractedText":"tres","summary":"","entities":[]}]""";
    Assert(item.Pages[0].PageNumber == 3, "Asignar PagesJson debe invalidar la caché.");
    item.Notes = [new DocumentNote("n1", "a", "r", DateTimeOffset.UnixEpoch, "nota", null)];
    Assert(ReferenceEquals(item.Notes, item.Notes) && item.Notes[0].Text == "nota");
    item.NotesJson = "[]";
    Assert(item.Notes.Count == 0, "Asignar NotesJson debe invalidar la caché.");
}));
await Check("Configuración: todos los appsettings*.json son JSON válido sin caracteres de control", () => Sync(() =>
{
    var root = AppContext.BaseDirectory;
    while (root is not null && !File.Exists(Path.Combine(root, "DocuSearch.sln"))) root = Path.GetDirectoryName(root);
    Assert(root is not null, "No se encontró la raíz del repositorio.");
    var files = Directory.GetFiles(root!, "appsettings*.json");
    Assert(files.Length >= 3, "Se esperaban appsettings.json, Development y Production.example.");
    foreach (var file in files)
    {
        var text = File.ReadAllText(file);
        Assert(!text.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t')), $"{Path.GetFileName(file)} contiene caracteres de control.");
        using var _ = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }
}));
await Check("Configuración: los valores por defecto pasan la validación de arranque", () => Sync(() =>
{
    IOptionsMonitor<T> Monitor<T>(T value) where T : class => new StaticOptionsMonitor<T>(value);
    Assert(new RagOptionsValidator(Monitor(new PineconeOptions())).Validate(null, new RagOptions()).Succeeded);
    Assert(new EmbeddingOptionsValidator().Validate(null, new EmbeddingOptions()).Succeeded);
    Assert(new PineconeOptionsValidator(Monitor(new EmbeddingOptions())).Validate(null, new PineconeOptions { Enabled = true }).Succeeded);
    Assert(new ResilienceOptionsValidator().Validate(null, new ResilienceOptions()).Succeeded);
    var auth = new AuthOptions { Users = [new() { Email = "a@example.test", Role = "auditor" }] };
    Assert(new AuthOptionsValidator().Validate(null, auth).Succeeded);
}));
await Check("Configuración: rechaza valores sin sentido y desajustes entre secciones", () => Sync(() =>
{
    IOptionsMonitor<T> Monitor<T>(T value) where T : class => new StaticOptionsMonitor<T>(value);
    string Errors(ValidateOptionsResult result) => result.Succeeded ? "" : string.Join(" | ", result.Failures!);
    var rag = Errors(new RagOptionsValidator(Monitor(new PineconeOptions())).Validate(null,
        new RagOptions { TopK = 0, OverlapTokens = 600, ChunkingStrategy = "random", VectorStore = "pinecone" }));
    Assert(rag.Contains("Rag:TopK") && rag.Contains("Rag:OverlapTokens") && rag.Contains("Rag:ChunkingStrategy") &&
        rag.Contains("Pinecone:Enabled=true"), rag);
    var pinecone = Errors(new PineconeOptionsValidator(Monitor(new EmbeddingOptions { Dimensions = 768 }))
        .Validate(null, new PineconeOptions { Enabled = true, Dimension = 1024 }));
    Assert(pinecone.Contains("debe coincidir con Embeddings:Dimensions (768)"), pinecone);
    Assert(new PineconeOptionsValidator(Monitor(new EmbeddingOptions { Dimensions = 768 }))
        .Validate(null, new PineconeOptions { Enabled = false, Dimension = 1024 }).Succeeded, "Pinecone apagado no se valida.");
    var auth = Errors(new AuthOptionsValidator().Validate(null, new AuthOptions
    {
        Users = [new() { Email = "a@example.test", Role = "root" }, new() { Email = "A@example.test", Role = "auditor" }]
    }));
    Assert(auth.Contains("«root» no existe") && auth.Contains("repite el correo"), auth);
    Assert(!new ResilienceOptionsValidator().Validate(null, new ResilienceOptions { MaxAttempts = 0 }).Succeeded);
}));
await Check("Ejemplos no inventan archivos PDF originales",() => Sync(() => Assert(seed.Documents.All(d => !d.HasOriginal))));
await Check("Datos de ejemplo válidos y departamentos referenciados", () => Sync(() =>
{
    foreach (var value in seed.Documents.Cast<object>().Concat(seed.Users).Concat(seed.Departments))
        Validator.ValidateObject(value, new ValidationContext(value), true);
    Assert(seed.Documents.All(d => seed.Departments.Any(x => x.Name == d.Department)));
}));
await Check("Administrador lee y descarga secretos", () => Sync(() => Assert(Permissions.CanRead(admin, secret) && Permissions.CanDownload(admin, secret))));
await Check("Consultor no lee documentos confidenciales ni secretos", () => Sync(() => Assert(!Permissions.CanRead(consultant, contract) && !Permissions.CanRead(consultant, secret))));
await Check("Consultor lee documentos internos", () => Sync(() => Assert(Permissions.CanRead(consultant, policy))));
await Check("Auditor lee secretos, no puede descargarlos explícitamente", () => Sync(() => Assert(Permissions.CanRead(auditor, secret) && !Permissions.CanDownload(auditor, secret))));
await Check("Administrador departamental solo lee su departamento", () => Sync(() => Assert(Permissions.CanRead(deptAdmin, contract) && !Permissions.CanRead(deptAdmin, policy))));
await Check("Gestor no asigna clasificación secreta", () => Sync(() => Assert(!Permissions.CanAssign(manager, manager.Department, "Estrictamente Secreto"))));
await Check("Consultor no carga documentos", () => Sync(() => Assert(!Permissions.CanWrite(consultant) && !Permissions.CanAssign(consultant, consultant.Department, "Público"))));
await Check("Rol desconocido falla cerrado", () => Sync(() => Assert(!Permissions.CanRead(new AppUser { Role = "unknown" }, policy))));
await Check("Clasificación desconocida falla cerrada", () => Sync(() => Assert(!Permissions.CanRead(admin, new DocumentItem { Confidentiality = "unknown" }))));
await Check("Normalización de acentos", () => Sync(() => Assert(TextAnalysis.Normalize("Política PÚBLICA") == "politica publica")));
await Check("Búsqueda exacta localiza fuente y página", () => Sync(() => Assert(LocalSearch.Search(seed.Documents, "$450,000 USD", true).Any(h => h.Document.Id == contract.Id && h.PageNumber == 4))));
await Check("Búsqueda contextual localiza norma y contraseñas", () => Sync(() => Assert(LocalSearch.Search(seed.Documents, "ISO 27001 contraseñas").First().Document.Id == policy.Id)));
await Check("Búsqueda plural encuentra contrato", () => Sync(() => Assert(LocalSearch.Search(seed.Documents, "contratos").Any(h => h.Document.Id == contract.Id))));
await Check("Consulta vacía lista páginas accesibles", () => Sync(() => Assert(LocalSearch.Search(seed.Documents, "").Count == 10)));
await Check("Consulta sin coincidencias no inventa resultados", () => Sync(() => Assert(LocalSearch.Search(seed.Documents, "zzzzzzzzz").Count == 0)));
await Check("Umbral de relevancia se aplica realmente", () => Sync(() => Assert(LocalSearch.Search(seed.Documents, "SLA inexistente", minimum: 100).Count == 0)));
await Check("Filtros combinados de departamento, categoría y año", () => Sync(() =>
{
    var filter = new DocumentFilter { Department = contract.Department, Category = "Contrato", Year = "2025" };
    Assert(filter.Apply(seed.Documents).Single().Id == contract.Id);
}));
await Check("Catálogo busca palabras clave y etiquetas sin perder filtros", () => Sync(() =>
{
    var document = new DocumentItem { Title = "Expediente", Keywords = ["Cláusula especial"], Tags = ["Pendiente"], Department = "Legal", Category = "Contrato", Confidentiality = "Uso Interno" };
    var filter = new DocumentFilter { Text = "clausula", Department = "Legal", Category = "Contrato", Level = "Uso Interno" };
    Assert(filter.Apply([document]).Single() == document);
    filter.Text = "pendiente";
    Assert(filter.Apply([document]).Single() == document);
    filter.Department = "Finanzas";
    Assert(!filter.Apply([document]).Any());
    Assert(new DocumentFilter().Apply([document]).Single() == document);
}));
await Check("Asistente: resumen por código y referencias a todas las páginas", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Resume CNT-2025-0892", seed.Documents, new());
    Assert(answer.Text.Contains("$450,000 USD") && answer.Sources.Count == 3);
}));
await Check("Asistente: entidades monetarias con página correcta", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Extrae montos de FIN-2024-Q4-01", seed.Documents, new());
    Assert(answer.Text.Contains("$12,450,000 USD") && answer.Sources.Any(h => h.PageNumber == 15));
}));
await Check("Asistente: consulta específica dentro de un expediente", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Cuáles son los ingresos operativos FIN-2024-Q4-01", seed.Documents, new());
    Assert(answer.Text.Contains("$12,450,000 USD") && answer.Sources.Any(h => h.PageNumber == 15));
}));
await Check("Asistente: recuperación de página exacta", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Página 8 de POL-2025-0012", seed.Documents, new());
    Assert(answer.Text.Contains("14 caracteres") && answer.Sources.Single().PageNumber == 8);
}));
await Check("Asistente: página no incluida no se inventa", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Página 9 de POL-2025-0012", seed.Documents, new());
    Assert(answer.Sources.Count == 0 && answer.Text.Contains("no tiene texto"));
}));
await Check("Asistente: seguimiento del expediente anterior", () => Sync(() =>
{
    var context = Session();
    _ = LocalAgent.AnswerFromSources("CNT-2025-0892", seed.Documents, context);
    var answer = LocalAgent.AnswerFromSources("Resume ese expediente", seed.Documents, context);
    Assert(answer.Text.Contains("Cloud") && context.LastDocumentCode == contract.InternalCode);
}));
await Check("Asistente: listado por confidencialidad", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Lista documentos públicos", seed.Documents, new());
    Assert(answer.Sources.Single().Document.Confidentiality == "Público");
}));
await Check("Asistente: consulta literal conserva cita verificable", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("Busca \"$450,000 USD\"", seed.Documents, new());
    Assert(answer.Sources.Any(h => h.Document.Id == contract.Id && h.PageNumber == 4));
}));
await Check("Asistente: no expone fuentes fuera del conjunto autorizado", () => Sync(() =>
{
    var readable = seed.Documents.Where(d => Permissions.CanRead(consultant, d)).ToList();
    var answer = LocalAgent.AnswerFromSources("Resume FIN-2024-Q4-01", readable, new());
    Assert(answer.Sources.Count == 0 && !answer.Text.Contains("EBITDA"));
}));
await Check("Asistente: ausencia de fuentes informada", () => Sync(() =>
{
    var answer = LocalAgent.AnswerFromSources("zzzzzzzzz", seed.Documents, new());
    Assert(answer.Sources.Count == 0 && answer.Text.Contains("No hay fuentes"));
}));
await Check("Asistente: consulta vacía rechazada explícitamente", () => Sync(() =>
    Throws<ValidationException>(() => LocalAgent.AnswerFromSources(" ", seed.Documents, new()))));
await Check("Asistente: límite de 2000 caracteres", () => Sync(() =>
    Throws<ValidationException>(() => LocalAgent.AnswerFromSources(new string('a', 2001), seed.Documents, new()))));
await Check("CSV escapa comillas y fórmulas", () => Sync(() =>
{
    Assert(ReportExport.CsvCell("=HYPERLINK(\"x\")") == "\"'=HYPERLINK(\"\"x\"\")\"");
    Assert(ReportExport.CsvCell("a,b") == "\"a,b\"");
    Assert(ReportExport.CsvCell("  +123").StartsWith("\"'"));
}));
await Check("Catálogo CSV incluye BOM y cabeceras", () => Sync(() =>
{
    var bytes = ReportExport.CatalogCsv(seed.Documents);
    Assert(bytes.Take(3).SequenceEqual(new byte[] { 239, 187, 191 }) && Encoding.UTF8.GetString(bytes).Contains(contract.InternalCode));
}));
await Check("Informe HTML codifica datos y no interpreta contenido como código", () => Sync(() =>
{
    var html = Encoding.UTF8.GetString(ReportExport.Html([new DocumentItem { Title = "<script>alert(1)</script>" }], "<img>"));
    Assert(!html.Contains("<script>") && html.Contains("&lt;script&gt;") && html.Contains("@media print"));
}));
await Check("PDF real: extracción, páginas y SHA-256", () => Sync(() =>
{
    var document = new DocumentItem();
    processor.Process(document, pdf);
    Assert(document.HasOriginal && document.OcrStatus == "Completado" && document.PageCount == 1);
    Assert(document.Pages[0].ExtractedText.Contains("99.99%"));
    Assert(document.Sha256Hash == Convert.ToHexStringLower(SHA256.HashData(pdf)));
}));
await Check("PDF con página sin texto requiere OCR", () => Sync(() =>
{
    var document = new DocumentItem();
    processor.Process(document, PdfFixture.Create(""));
    Assert(document.OcrStatus == "Requiere OCR" && document.ExecutiveSummary.Contains("sin capa de texto"));
}));
await Check("PDF mixto no oculta la necesidad de OCR", () => Sync(() =>
{
    var document = new DocumentItem();
    processor.Process(document, PdfFixture.Create("Page with readable text and a service contract.", ""));
    Assert(document.PageCount == 2 && document.OcrStatus == "Requiere OCR");
}));
await Check("Cabecera PDF inválida rechazada", () => Sync(() => Throws<InvalidDataException>(() => processor.Process(new(), Encoding.UTF8.GetBytes("not a pdf")))));
await Check("PDF vacío rechazado", () => Sync(() => Throws<InvalidDataException>(() => processor.Process(new(), []))));
await Check("Límite de 25 MB medido en bytes", () => Sync(() => Throws<InvalidDataException>(() => processor.Process(new(), new byte[PdfProcessor.MaxFileBytes + 1]))));
await Check("PDF de exactamente 25 MB admitido", () => Sync(() =>
{
    var padding = (int)PdfProcessor.MaxFileBytes - PdfFixture.Create("boundary").Length - 2;
    var boundary = PdfFixture.CreateWithPadding(padding, "boundary");
    padding += (int)(PdfProcessor.MaxFileBytes - boundary.LongLength);
    boundary = PdfFixture.CreateWithPadding(padding, "boundary");
    Assert(boundary.LongLength == PdfProcessor.MaxFileBytes);
    var document = new DocumentItem();
    processor.Process(document, boundary);
    Assert(document.FileSizeBytes == PdfProcessor.MaxFileBytes);
}));
await Check("Límite de 500 páginas real", () => Sync(() => Throws<InvalidDataException>(() => processor.Process(new(), PdfFixture.Create(Enumerable.Repeat("", 501).ToArray())))));
await Check("PDF de exactamente 500 páginas admitido", () => Sync(() =>
{
    var document = new DocumentItem();
    processor.Process(document, PdfFixture.Create(Enumerable.Repeat("", 500).ToArray()));
    Assert(document.PageCount == 500 && document.Pages.Count == 500 && document.OcrStatus == "Requiere OCR");
}));
await Check("Entidades monetarias conservan página de origen", () => Sync(() => Assert(TextAnalysis.Entities("Annual price $450000 USD.", 4).Any(e => e.Type == "MONEY" && e.PageNumber == 4))));
await Check("Páginas y notas sobreviven serialización JSON", () => Sync(() =>
{
    var copy = JsonSerializer.Deserialize<DocumentItem>(JsonSerializer.Serialize(contract, Catalog.Json), Catalog.Json)!;
    Assert(copy.Pages.Count == contract.Pages.Count && copy.Notes.Count == contract.Notes.Count);
    Assert(!JsonSerializer.Serialize(contract, Catalog.Json).Contains("pagesJson"));
}));
await Check("Importación rechaza versiones desconocidas antes de acceder a la base", async () =>
{
    var factory = new TestFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=127.0.0.1,1;Database=unreachable;Integrated Security=True;Connect Timeout=1", sql => sql.UseCompatibilityLevel(150)).Options);
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"schemaVersion\":99}"));
    await ThrowsAsync<InvalidDataException>(() => new LegacyImport(factory, Session(), processor).ImportAsync(stream));
});
await Check("Importación rechaza más de 100 expedientes antes de acceder a la base", async () =>
{
    var factory = new TestFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=127.0.0.1,1;Database=unreachable;Integrated Security=True;Connect Timeout=1", sql => sql.UseCompatibilityLevel(150)).Options);
    using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, documents = Enumerable.Range(0, 101).Select(_ => contract), users = new AppUser[0], departments = new DepartmentInfo[0] }, Catalog.Json));
    await ThrowsAsync<InvalidDataException>(() => new LegacyImport(factory, Session(), processor).ImportAsync(stream));
});
await Check("Modelo y migración SQL Server 2019 se generan sin proveedor alternativo", () => Sync(() =>
{
    using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=localhost;Database=offline;Integrated Security=True", sql => sql.UseCompatibilityLevel(150)).Options);
    var schema = db.Database.GenerateCreateScript();
    var migration = db.GetService<IMigrator>().GenerateScript();
    Assert(schema.Contains("nvarchar(max)") && schema.Contains("varbinary(max)") && migration.Contains("ISJSON([PagesJson])"));
    Assert(migration.Contains("CREATE TABLE [Documents]") && migration.Contains("CREATE TABLE [AgentTraces]"));
}));
await Check("Error SQL Server envuelto se informa en español y libera estado ocupado", async () =>
{
    var factory = new TestFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=localhost;Database=offline;Integrated Security=True", sql => sql.UseCompatibilityLevel(150)).Options);
    var service = new DocumentService(factory, Session(), processor);
    var state = new WorkspaceState(service, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceState>.Instance);
    await state.RunAsync(() => Task.FromException(new InvalidOperationException("Transient failure", new TestDbException())));
    Assert(!state.Busy && state.Data is null && state.Error!.StartsWith("No se pudo completar la operación en SQL Server"));
});

await Check("Observabilidad: esquema sin contenido ni identidad", () => Sync(() =>
{
    var names = typeof(AgentTrace).GetProperties().Select(p => p.Name).Order().ToArray();
    Assert(names.SequenceEqual(new[] { "Id", "StartedUtc", "Engine", "Model", "Status", "ErrorCode", "DurationMs", "InputTokens", "OutputTokens" }.Order()));
    Assert(new AgentTrace().InputTokens is null && new AgentTrace().OutputTokens is null);
}));
await Check("Observabilidad: errores categorizados sin mensaje sensible", () => Sync(() =>
{
    Assert(AgentObservability.ErrorCode(new UnauthorizedAccessException("secret")) == "access_denied");
    Assert(AgentObservability.ErrorCode(new ValidationException("secret")) == "invalid_input");
    Assert(AgentObservability.ErrorCode(new InvalidOperationException("secret", new TaskCanceledException("secret"))) == "timeout");
    Assert(AgentObservability.ErrorCode(new InvalidOperationException("secret", new HttpRequestException("secret"))) == "connection");
}));
if (args.Contains("--opencode"))
{
    var url = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_URL")
        ?? throw new InvalidOperationException("Configura DOCUSEARCH_OPENCODE_URL con el servidor OpenCode (p. ej. http://host.docker.internal:4097).");
    var password = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_PASSWORD") ?? "";
    var provider = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_PROVIDER") ?? "deepseek";
    var model = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_MODEL") ?? "deepseek-flash";
    var agent = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_AGENT") ?? "zodiac-asistente";
    using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    var client = new OpenCodeClient(http, Options.Create(new OpenCodeOptions
    {
        Enabled = true, BaseUrl = url, Password = password, AllowNonLoopback = true,
        Agent = agent, ProviderId = provider, ModelId = model, TimeoutSeconds = 180
    }));
    await Check("OpenCode real: salud y agente disponibles", async () =>
    {
        await client.CheckAsync();
        Console.WriteLine($"INFO OpenCode real conectado: {client.Engine} · agente {client.Agent}");
    });
    await Check("OpenCode real: catálogo de gratis y locales", async () =>
    {
        var catalog = await client.FetchCatalogAsync();
        var connected = catalog.Connected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locals = catalog.Providers
            .Where(provider => provider.Id is "lmstudio" or "llamacpp")
            .SelectMany(provider => provider.Models).Count();
        var free = catalog.Providers
            .Where(provider => connected.Contains(provider.Id) && provider.Id is not ("lmstudio" or "llamacpp"))
            .SelectMany(provider => provider.Models)
            .Count(candidate => candidate.InputCost == 0 && candidate.OutputCost == 0);
        Assert(free > 0 || locals > 0);
        Console.WriteLine($"INFO OpenCode real: {catalog.Providers.Count} proveedores, {connected.Count} conectados, {free} gratis, {locals} locales");
    });
    await Check("OpenCode real: genera respuesta con cita autorizada", async () =>
    {
        var document = new DocumentItem { InternalCode = "TEST-2026-OPENCODE", Title = "Prueba sintética OpenCode" };
        var answer = await client.GenerateAsync("¿Qué disponibilidad garantiza el contrato?",
            [new(document, 1, "El contrato garantiza una disponibilidad SLA de 99.99%.", 100)]);
        Assert(answer.Text.Length > 0 && answer.Sources.Single().Document.Id == document.Id);
        Console.WriteLine($"INFO OpenCode real: {answer.Provider}; respuesta: {answer.Text}");
    });
}
if (args.Contains("--langchain-opencode"))
{
    var url = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_URL")
        ?? throw new InvalidOperationException("Configura DOCUSEARCH_OPENCODE_URL con el servidor OpenCode.");
    var password = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_PASSWORD") ?? "";
    var provider = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_PROVIDER") ?? "deepseek";
    var model = Environment.GetEnvironmentVariable("DOCUSEARCH_OPENCODE_MODEL") ?? "deepseek-flash";
    var python = Environment.GetEnvironmentVariable("DOCUSEARCH_PYTHON") ?? "python3";
    using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
    var client = new OpenCodeClient(http, Options.Create(new OpenCodeOptions
    {
        Enabled = true, BaseUrl = url, Password = password, AllowNonLoopback = true,
        ProviderId = provider, ModelId = model, TimeoutSeconds = 180,
        UseLangChain = true, PythonExecutable = python
    }));
    await Check("LangChain + OpenCode real: genera con citas y OpenCode", async () =>
    {
        await client.CheckAsync();
        var document = new DocumentItem { InternalCode = "TEST-2026-LANGCHAIN", Title = "Prueba LangChain" };
        var answer = await client.GenerateAsync("¿Qué disponibilidad garantiza el contrato?",
            [new(document, 1, "El contrato garantiza una disponibilidad SLA de 99.99%.", 100)]);
        Assert(answer.Text.Length > 0 && answer.Provider.Contains("LangChain", StringComparison.Ordinal) &&
            answer.Sources.Single().Document.Id == document.Id);
        Console.WriteLine($"INFO LangChain + OpenCode real: {answer.Provider}; respuesta: {answer.Text}");
    });
}
await Check("TOTP: vectores RFC 6238 con 6 dígitos", () => Sync(() =>
{
    const string rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"; // "12345678901234567890" en Base32
    Assert(Totp.Generate(rfcSecret, DateTimeOffset.FromUnixTimeSeconds(59)) == "287082");
    Assert(Totp.Generate(rfcSecret, DateTimeOffset.FromUnixTimeSeconds(1111111109)) == "081804");
    Assert(Totp.Generate(rfcSecret, DateTimeOffset.FromUnixTimeSeconds(1234567890)) == "005924");
}));
await Check("TOTP: ventana de reloj y rechazo de códigos inválidos", () => Sync(() =>
{
    const string rfcSecret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
    Assert(Totp.TryVerify(rfcSecret, "081804", now, 1, out var step) && step == Totp.StepAt(now));
    var previous = Totp.Generate(rfcSecret, now.AddSeconds(-Totp.PeriodSeconds));
    Assert(Totp.TryVerify(rfcSecret, previous, now, 1, out var previousStep) && previousStep == Totp.StepAt(now) - 1);
    Assert(!Totp.TryVerify(rfcSecret, previous, now, 0, out _));
    Assert(!Totp.TryVerify(rfcSecret, "12345", now, 1, out _));
    Assert(!Totp.TryVerify(rfcSecret, "abcdef", now, 1, out _));
    Assert(!Totp.TryVerify(rfcSecret, "", now, 1, out _));
}));
await Check("TOTP: Base32 tolera minúsculas y separadores y se formatea en grupos", () => Sync(() =>
{
    Assert(Totp.Generate("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", DateTimeOffset.FromUnixTimeSeconds(59)) == "287082");
    Assert(Totp.FormatSecret("GEZDGNBVGY3TQOJQ") == "GEZD GNBV GY3T QOJQ");
    var uri = Totp.OtpAuthUri("DocuSearch AI", "admin.demo@example.test", "VL45YEQOYLCSP6TQHG563ULJZBMOO3ZG");
    Assert(uri.StartsWith("otpauth://totp/", StringComparison.Ordinal) && uri.Contains("secret=VL45YEQO", StringComparison.Ordinal));
}));
await Check("OpenCode: exige loopback o contraseña para servidores remotos", () => Sync(() =>
{
    Throws<InvalidOperationException>(() => _ = new OpenCodeClient(new HttpClient(),
        Options.Create(new OpenCodeOptions { Enabled = true, BaseUrl = "http://192.168.1.10:4097" })));
    Throws<InvalidOperationException>(() => _ = new OpenCodeClient(new HttpClient(),
        Options.Create(new OpenCodeOptions { Enabled = true, BaseUrl = "http://192.168.1.10:4097", AllowNonLoopback = true })));
    var remote = new OpenCodeClient(new HttpClient(), Options.Create(new OpenCodeOptions
    {
        Enabled = true, BaseUrl = "http://192.168.1.10:4097", AllowNonLoopback = true, Password = "x",
        ProviderId = "deepseek", ModelId = "deepseek-flash"
    }));
    Assert(remote.Enabled && remote.IsRemote);
}));
await Check("Resiliencia: reintenta fallos transitorios con backoff", async () =>
{
    var attempts = 0;
    var policy = new AgentResilience(Options.Create(new ResilienceOptions
    {
        MaxAttempts = 3, BaseDelayMs = 1, MaxDelayMs = 2, FailureThreshold = 5, BreakSeconds = 1, AttemptTimeoutSeconds = 5
    }), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentResilience>.Instance);
    var result = await policy.ExecuteAsync("test.retry", _ =>
    {
        attempts++;
        if (attempts < 3) throw new TransientAgentException("fallo temporal");
        return Task.FromResult(42);
    });
    Assert(result == 42 && attempts == 3);
});
await Check("Resiliencia: el circuito abre tras fallos consecutivos y corta llamadas", async () =>
{
    var attempts = 0;
    var policy = new AgentResilience(Options.Create(new ResilienceOptions
    {
        MaxAttempts = 1, BaseDelayMs = 1, MaxDelayMs = 2, FailureThreshold = 2, BreakSeconds = 30, AttemptTimeoutSeconds = 5
    }), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentResilience>.Instance);
    for (var i = 0; i < 2; i++)
        await ThrowsAsync<AgentUnavailableException>(() => policy.ExecuteAsync<int>("test.circuit", _ =>
        {
            attempts++;
            throw new TransientAgentException("caída");
        }));
    var before = attempts;
    await ThrowsAsync<CircuitOpenException>(() => policy.ExecuteAsync("test.circuit", _ =>
    {
        attempts++;
        return Task.FromResult(1);
    }));
    Assert(attempts == before && policy.Describe("test.circuit").StartsWith("open", StringComparison.Ordinal));
});
await Check("Resiliencia: timeout por intento y parada tras agotar reintentos", async () =>
{
    var policy = new AgentResilience(Options.Create(new ResilienceOptions
    {
        MaxAttempts = 1, BaseDelayMs = 1, MaxDelayMs = 2, FailureThreshold = 5, BreakSeconds = 1, AttemptTimeoutSeconds = 1
    }), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentResilience>.Instance);
    var timer = System.Diagnostics.Stopwatch.StartNew();
    await ThrowsAsync<AgentUnavailableException>(() => policy.ExecuteAsync("test.timeout", async token =>
    {
        await Task.Delay(TimeSpan.FromSeconds(5), token);
        return 1;
    }));
    Assert(timer.Elapsed < TimeSpan.FromSeconds(4));
});
await Check("OpenCode: comprueba salud y agente configurado", async () =>
{
    var calls = new List<string>();
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}", request => calls.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}"));
    await OpenCodeFor(http).CheckAsync();
    Assert(calls.Contains("GET /api/info") && calls.Contains("GET /api/agent"));
});
await Check("OpenCode: genera respuesta con citas, tokens y autenticación", async () =>
{
    string? sessionBody = null, promptBody = null, auth = null;
    var calls = new List<string>();
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}", request =>
    {
        calls.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
        if (request.RequestUri.AbsolutePath == "/api/session") sessionBody = body;
        if (request.RequestUri.AbsolutePath.EndsWith("/prompt", StringComparison.Ordinal)) promptBody = body;
        auth = request.Headers.Authorization?.ToString() ?? auth;
    });
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    var answer = await OpenCodeFor(http).GenerateAsync("¿Qué SLA garantiza el contrato?", [new(document, 4, "SLA 99.99%", 100)]);
    Assert(answer.Text == "ok" && answer.Sources.Single().PageNumber == 4);
    Assert(answer.Provider.Contains("OpenCode", StringComparison.Ordinal) && answer.InputTokens == 10 && answer.OutputTokens == 5);
    Assert(sessionBody is not null && sessionBody.Contains("\"agent\":\"zodiac-asistente\"", StringComparison.Ordinal) &&
        sessionBody.Contains("\"providerID\":\"deepseek\"", StringComparison.Ordinal) &&
        sessionBody.Contains("\"id\":\"deepseek-flash\"", StringComparison.Ordinal) &&
        sessionBody.Contains("\"effect\":\"deny\"", StringComparison.Ordinal));
    Assert(promptBody is not null && promptBody.Contains("SLA", StringComparison.Ordinal) &&
        promptBody.Contains("CNT-2025-0892", StringComparison.Ordinal));
    Assert(auth is not null && auth.StartsWith("Basic ", StringComparison.Ordinal));
    Assert(calls.Contains("DELETE /api/session/ses_test"));
});
await Check("OpenCode: bloquea citas fuera de las fuentes autorizadas", async () =>
{
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[2]}");
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    await ThrowsAsync<InvalidOperationException>(() => OpenCodeFor(http).GenerateAsync("q",
        [new(document, 1, "Solo una fuente", 100)]));
});
await Check("OpenCode: rechaza respuestas sin JSON estructurado", async () =>
{
    using var http = OpenCodeMock("esto no es json");
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    await ThrowsAsync<InvalidOperationException>(() => OpenCodeFor(http).GenerateAsync("q",
        [new(document, 1, "Solo una fuente", 100)]));
});
await Check("OpenCode: sin fuentes no llama al servidor", async () =>
{
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}");
    await ThrowsAsync<InvalidOperationException>(() => OpenCodeFor(http).GenerateAsync("q", []));
});
await Check("OpenCode: informa credenciales rechazadas (401)", async () =>
{
    using var http = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
    var rejected = false;
    try { await OpenCodeFor(http).CheckAsync(); }
    catch (InvalidOperationException ex)
    {
        rejected = ex.Message.Contains("401", StringComparison.Ordinal) || ex.Message.Contains("credenciales", StringComparison.OrdinalIgnoreCase);
    }
    Assert(rejected);
});
await Check("OpenCode: usa el modelo del perfil seleccionado", async () =>
{
    string? body = null;
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}", request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/session")
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    });
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    var answer = await OpenCodeFor(http).GenerateAsync("q", [new(document, 1, "texto", 100)],
        "deepseek", "deepseek-flash", "zodiac-asistente");
    Assert(body is not null && body.Contains("\"providerID\":\"deepseek\"", StringComparison.Ordinal) &&
        body.Contains("\"id\":\"deepseek-flash\"", StringComparison.Ordinal));
    Assert(answer.Provider.Contains("deepseek/deepseek-flash", StringComparison.Ordinal));
});
await Check("Asistente: catálogo dinámico separa gratis, pago y oculta locales", async () =>
{
    var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(OpenCodeCatalogMock(catalogProvidersJson, catalogModelsJson)),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
    await runtime.RefreshAsync(force: true);
    var locals = runtime.Profiles.Where(profile => profile.Group == "Locales").ToList();
    var free = runtime.Profiles.Where(profile => profile.Group == "Gratis").ToList();
    var paid = runtime.Profiles.Where(profile => profile.Group == "Pago").ToList();
    Assert(locals.Count == 0 && free.Count == 0 && paid.Count == 1);
    Assert(paid.Single().ModelId == "paid-model" && paid.Single().Available);
    Assert(runtime.Active.Group == "Pago" && runtime.Active.ModelId == "paid-model");
    Assert(!runtime.Profiles.Any(profile => profile.ModelId == "free-model"));

    var withFree = new AssistantRuntime(Options.Create(new AssistantOptions { IncludeFreeModels = true }),
        OpenCodeFor(OpenCodeCatalogMock(catalogProvidersJson, catalogModelsJson)),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
    await withFree.RefreshAsync(force: true);
    var freeEnabled = withFree.Profiles.Where(profile => profile.Group == "Gratis").ToList();
    Assert(freeEnabled.Count == 2 && freeEnabled.Any(p => p.ModelId == "free-model") && freeEnabled.Any(p => p.ModelId == "deepseek-flash"));
    Assert(withFree.TrySelect(freeEnabled[0].Id, out var selected) && selected!.Group == "Gratis");

    Assert(!runtime.TrySelect("local:no/existe", out _));
    Assert(runtime.Profiles.Any(profile => profile.Engine == "extractive"));
});
await Check("Asistente: sin OpenCode queda solo el modo extractivo", async () =>
{
    var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(new HttpClient(), enabled: false),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
    await runtime.RefreshAsync(force: true);
    Assert(runtime.Profiles.Count == 1 && runtime.Active.Engine == "extractive" && runtime.LastError is not null);
});
await Check("Asistente: conserva el catálogo anterior si OpenCode falla después", async () =>
{
    var fail = false;
    using var http = new HttpClient(new TestHttpHandler((request, _) =>
    {
        if (fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var path = request.RequestUri!.AbsolutePath;
        return Task.FromResult(JsonResponse(path switch
        {
            "/api/info" => """{"version":"2.2.5"}""",
            "/api/provider" => catalogProvidersJson,
            "/api/model" => catalogModelsJson,
            _ => "{}"
        }));
    }));
    var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(http),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
    await runtime.RefreshAsync(force: true);
    var previous = runtime.Profiles.Count;
    Assert(previous > 1);
    fail = true;
    await runtime.RefreshAsync(force: true);
    Assert(runtime.Profiles.Count == previous && runtime.LastError is not null);
});
await Check("OpenCode: desactiva herramientas y omite agente vacío", async () =>
{
    string? body = null;
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}", request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/session")
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    });
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    await OpenCodeFor(http, agent: "").GenerateAsync("q", [new(document, 1, "texto", 100)]);
    Assert(body is not null && body.Contains("\"permissions\":[{\"action\":\"*\",\"resource\":\"*\",\"effect\":\"deny\"}]", StringComparison.Ordinal));
    Assert(!body!.Contains("\"agent\":", StringComparison.Ordinal));
});
await Check("OpenCode 2: rechaza un servidor incompatible", async () =>
{
    using var http = new HttpClient(new TestHttpHandler((_, _) =>
        Task.FromResult(JsonResponse("""{"healthy":true}"""))));
    await ThrowsAsync<InvalidOperationException>(() => OpenCodeFor(http).CheckAsync());
});
await Check("OpenCode: el modo LangChain se refleja en el motor", () => Sync(() =>
{
    var client = new OpenCodeClient(new HttpClient(), Options.Create(new OpenCodeOptions
    {
        Enabled = true, BaseUrl = "http://127.0.0.1:4097", Password = "secreto", Agent = "",
        ProviderId = "opencode", ModelId = "free-model", UseLangChain = true, PythonExecutable = "python"
    }));
    Assert(client.UseLangChain && client.Engine.Contains("LangChain", StringComparison.Ordinal));
}));
if (args.Contains("--verifier"))
{
    var verifierDocument = new DocumentItem
    {
        InternalCode = "VER-2026-0001", Title = "Contrato de verificación", Author = "Pruebas",
        Department = "Tecnología & TI", Confidentiality = "Uso Interno",
        Pages = [new DocumentPage(1, "El contrato garantiza un SLA del 99.99%.", "SLA 99.99% del contrato.", [])]
    };
    var secretDocument = new DocumentItem
    {
        InternalCode = "VER-2026-SECRET", Title = "Documento secreto de verificación", Author = "Pruebas",
        Department = "Legal & Contratos", Confidentiality = "Estrictamente Secreto",
        Pages = [new DocumentPage(1, "Contenido estrictamente secreto.", "Contenido reservado.", [])]
    };
    var verifierDocuments = new List<DocumentItem> { verifierDocument, secretDocument };
    var verifierAdmin = admin;
    SearchHit Source(string snippet) => new(verifierDocument, 1, snippet, 100);
    var validSource = Source("El contrato garantiza un SLA del 99.99%.");
    var validAnswer = new AgentAnswer("El SLA es 99.99%.", [validSource]);

    await Check("Verificador AG-02: acepta una respuesta con fuente y página reales", () => Sync(() =>
    {
        var result = AgentVerifier.Verify(validAnswer, verifierDocuments, verifierAdmin);
        Assert(result.IsValid, string.Join(",", result.Issues.Select(issue => issue.Code)));
    }));
    await Check("Verificador AG-02: rechaza respuesta vacía o demasiado larga", () => Sync(() =>
    {
        var empty = AgentVerifier.Verify(new AgentAnswer(" ", [validSource]), verifierDocuments, verifierAdmin);
        Assert(!empty.IsValid && empty.Issues.Any(issue => issue.Code == VerificationCode.Empty));
        var tooLong = AgentVerifier.Verify(
            new AgentAnswer(new string('a', AgentVerifier.MaxAnswerLength + 1), [validSource]),
            verifierDocuments, verifierAdmin);
        Assert(!tooLong.IsValid && tooLong.Issues.Any(issue => issue.Code == VerificationCode.TooLong));
    }));
    await Check("Verificador AG-02: exige al menos una cita", () => Sync(() =>
    {
        var result = AgentVerifier.Verify(new AgentAnswer("Respuesta sin fuentes", []), verifierDocuments, verifierAdmin);
        Assert(!result.IsValid && result.Issues.Any(issue => issue.Code == VerificationCode.NoCitations));
    }));
    await Check("Verificador AG-02: rechaza fuentes no autorizadas", () => Sync(() =>
    {
        var secretPage = secretDocument.Pages[0];
        var answer = new AgentAnswer("x", [new SearchHit(secretDocument, secretPage.PageNumber, secretPage.ExtractedText, 100)]);
        var result = AgentVerifier.Verify(answer, verifierDocuments, consultant);
        Assert(!result.IsValid && result.Issues.Any(issue => issue.Code == VerificationCode.UnauthorizedSource));
    }));
    await Check("Verificador AG-02: rechaza páginas inexistentes", () => Sync(() =>
    {
        var result = AgentVerifier.Verify(new AgentAnswer("x", [new SearchHit(verifierDocument, 2, "texto", 100)]), verifierDocuments, verifierAdmin);
        Assert(!result.IsValid && result.Issues.Any(issue => issue.Code == VerificationCode.PageMissing));
    }));
    await Check("Verificador AG-02: rechaza pares documento/página duplicados", () => Sync(() =>
    {
        var result = AgentVerifier.Verify(new AgentAnswer("x", [validSource, validSource]), verifierDocuments, verifierAdmin);
        Assert(!result.IsValid && result.Issues.Any(issue => issue.Code == VerificationCode.DuplicatePair));
    }));
    await Check("Verificador AG-02: rechaza texto citado que no consta en la página", () => Sync(() =>
    {
        var result = AgentVerifier.Verify(new AgentAnswer("x", [Source("texto inexistente zzz")]), verifierDocuments, verifierAdmin);
        Assert(!result.IsValid && result.Issues.Any(issue => issue.Code == VerificationCode.TextNotInPage));
    }));
    await Check("Verificador AG-02: acepta un resumen extractivo de la página como evidencia", () => Sync(() =>
    {
        var result = AgentVerifier.Verify(new AgentAnswer("x", [new SearchHit(verifierDocument, 1, "SLA 99.99% del contrato.", 100)]), verifierDocuments, verifierAdmin);
        Assert(result.IsValid, string.Join(",", result.Issues.Select(issue => issue.Code)));
    }));
    await Check("Verificador AG-02: informa duplicados por SHA-256", () => Sync(() =>
    {
        var duplicates = AgentVerifier.FindDuplicateDocuments([
            new DocumentItem { InternalCode = "DUP-A", Sha256Hash = "abc" },
            new DocumentItem { InternalCode = "DUP-B", Sha256Hash = "abc" }
        ]);
        Assert(duplicates.Count == 1 && duplicates[0].DocumentCodes.SequenceEqual(["DUP-A", "DUP-B"]));
    }));
    await Check("Verificador AG-02: normaliza citas 1..N y rechaza fuera de rango", () => Sync(() =>
    {
        Assert(AgentVerifier.TryNormalizeCitations([2, 1, 2], 2, out var normalized, out var okCode) &&
               okCode == VerificationCode.Ok && normalized.SequenceEqual([2, 1]));
        Assert(!AgentVerifier.TryNormalizeCitations([0], 2, out _, out var badCode) && badCode == VerificationCode.BadCitation);
        Assert(!AgentVerifier.TryNormalizeCitations([], 2, out _, out var emptyCode) && emptyCode == VerificationCode.NoCitations);
    }));
}

if (args.Contains("--inspector"))
{
    var inspectorGood = new DocumentItem
    {
        InternalCode = "INS-2026-0001", Title = "Expediente íntegro", Author = "Pruebas",
        Department = "Tecnología & TI", Category = "Contrato", Confidentiality = "Uso Interno",
        HasOriginal = true, Sha256Hash = "aaa", OcrStatus = "Completado",
        ExecutiveSummary = "Resumen de prueba.", Keywords = ["calidad"]
    };
    var inspectorBad = new DocumentItem
    {
        InternalCode = "INS-2026-0002", Title = "", Author = "", Department = "Tecnología & TI",
        Category = "", Confidentiality = "Uso Interno", HasOriginal = false, Sha256Hash = "aaa",
        OcrStatus = "Requiere OCR", ExecutiveSummary = "", Keywords = []
    };
    var inspectorDocuments = new List<DocumentItem> { inspectorGood, inspectorBad };
    var inspectorHashes = new Dictionary<string, string> { [inspectorGood.Id] = "bbb" };
    var inspectorReport = DocumentInspector.Analyze(inspectorDocuments, inspectorHashes);

    await Check("Inspector AG-10: cuenta OCR, originales, metadatos e integridad", () => Sync(() =>
    {
        Assert(inspectorReport.TotalDocuments == 2 && inspectorReport.OcrPending == 1 &&
               inspectorReport.MissingOriginals == 1 && inspectorReport.MetadataGaps == 1 &&
               inspectorReport.IntegrityMismatches == 1);
        Assert(inspectorReport.Findings.Any(finding => finding.Kind == "integrity" && finding.Severity == "error"));
        Assert(inspectorReport.Findings.Any(finding => finding.Kind == "ocr"));
        Assert(inspectorReport.Findings.Any(finding => finding.Kind == "missing_original"));
    }));
    await Check("Inspector AG-10: detecta duplicados por SHA-256", () => Sync(() =>
    {
        Assert(inspectorReport.Duplicates.Count == 1 && inspectorReport.Duplicates[0].DocumentCodes.Contains("INS-2026-0001"));
        Assert(inspectorReport.Findings.Any(finding => finding.Kind == "duplicate"));
    }));
    await Check("Inspector AG-10: no verifica integridad si no se piden hashes", () => Sync(() =>
    {
        var withoutHashes = DocumentInspector.Analyze(inspectorDocuments);
        Assert(withoutHashes.IntegrityMismatches == 0 && withoutHashes.Findings.All(finding => finding.Kind != "integrity"));
    }));
    await Check("Inspector AG-10: detalla los metadatos incompletos", () => Sync(() =>
    {
        var metadata = inspectorReport.Findings.First(finding => finding.Kind == "metadata");
        Assert(metadata.DocumentCode == "INS-2026-0002" && metadata.Detail.Contains("título") && metadata.Detail.Contains("palabras clave"));
    }));
    await Check("Inspector AG-10: informe vacío sin documentos", () => Sync(() =>
    {
        var empty = DocumentInspector.Analyze([]);
        Assert(empty.TotalDocuments == 0 && empty.Findings.Count == 0 && empty.Duplicates.Count == 0);
    }));

    DocumentItem RagDoc(string code, params int[] textPages) => new()
    {
        InternalCode = code, Title = code, Department = "Tecnología & TI", Confidentiality = "Uso Interno",
        Pages = textPages.Select(page => new DocumentPage(page, $"Texto de la página {page}", "", [])).ToList()
    };
    var indexedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    var ragOk = RagDoc("RAG-OK", 1, 2);
    var ragGap = RagDoc("RAG-GAP", 1, 2, 3);
    var ragMissing = RagDoc("RAG-MISSING", 1);
    var ragOtherModel = RagDoc("RAG-MODEL", 1);
    var ragNoModel = RagDoc("RAG-NOMODEL", 1);
    var ragStale = RagDoc("RAG-STALE", 1);
    var ragScanned = RagDoc("RAG-SCAN");
    ragScanned.Pages = [new DocumentPage(1, "   ", "", [])];
    var ragDocuments = new List<DocumentItem> { ragOk, ragGap, ragMissing, ragOtherModel, ragNoModel, ragStale, ragScanned };
    var ragStats = new List<RagPageStat>
    {
        new(ragOk.Id, 1, "multilingual-e5-large", 1024, 2, indexedAt), new(ragOk.Id, 2, "multilingual-e5-large", 1024, 1, indexedAt),
        new(ragGap.Id, 1, "multilingual-e5-large", 1024, 1, indexedAt),
        new(ragOtherModel.Id, 1, "nomic-embed-text", 768, 3, indexedAt),
        new(ragNoModel.Id, 1, "", 1024, 2, indexedAt),
        new(ragStale.Id, 1, "multilingual-e5-large", 1024, 1, indexedAt)
    };
    var ragChanges = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase)
    {
        ["RAG-OK"] = indexedAt.AddMinutes(-1),
        ["RAG-STALE"] = indexedAt.AddDays(2)
    };
    var pageIndexed = ragDocuments.Where(document => document != ragGap).Select(document => document.Id).ToHashSet();
    var ragIndex = RagIndexInspector.Analyze(ragDocuments, ragStats, ragChanges, pageIndexed, "multilingual-e5-large", 1024);
    IEnumerable<InspectionFinding> RagFindings(string code) => ragIndex.Findings.Where(finding => finding.DocumentCode == code);

    await Check("Inspector RAG: expediente con texto sin fragmentos es un error", () => Sync(() =>
    {
        Assert(RagFindings("RAG-MISSING").Any(finding => finding.Kind == "rag-missing" && finding.Severity == "error"));
        Assert(ragIndex.ReindexDocumentIds.Contains(ragMissing.Id));
    }));
    await Check("Inspector RAG: lista las páginas con texto sin fragmentos y el índice por página ausente", () => Sync(() =>
    {
        var gap = RagFindings("RAG-GAP").Single(finding => finding.Kind == "rag-page-gap");
        Assert(gap.Detail.Contains("2, 3"), gap.Detail);
        Assert(RagFindings("RAG-GAP").Any(finding => finding.Kind == "page-index-missing"));
    }));
    await Check("Inspector RAG: modelo o dimensiones distintos es error; sin modelo es informativo", () => Sync(() =>
    {
        var other = RagFindings("RAG-MODEL").Single(finding => finding.Kind == "rag-model");
        Assert(other.Severity == "error" && other.Detail.Contains("nomic-embed-text/768"), other.Detail);
        Assert(RagFindings("RAG-NOMODEL").Single(finding => finding.Kind == "rag-model").Severity == "info");
    }));
    await Check("Inspector RAG: detecta índice anterior al último cambio de texto", () => Sync(() =>
    {
        Assert(RagFindings("RAG-STALE").Any(finding => finding.Kind == "rag-stale"));
        Assert(!RagFindings("RAG-OK").Any(), "Un índice posterior a la carga no es un hallazgo");
    }));
    await Check("Inspector RAG: un escaneado sin texto no da falso positivo y no cuenta como indexable", () => Sync(() =>
    {
        Assert(!RagFindings("RAG-SCAN").Any());
        Assert(ragIndex.IndexableDocuments == 6 && ragIndex.IndexedDocuments == 5 && ragIndex.TotalDocuments == 7);
        Assert(ragIndex.TextPages == 9 && ragIndex.CoveredPages == 6 && ragIndex.TotalChunks == 10);
        Assert(ragIndex.Findings[0].Severity == "error", "Los errores van primero");
    }));
    await Check("Inspector RAG: recomienda reindexar y queda correcto sin hallazgos", () => Sync(() =>
    {
        Assert(ragIndex.ReindexDocumentIds.Count == 5 && !ragIndex.ReindexDocumentIds.Contains(ragOk.Id));
        Assert(ragIndex.Recommendation.Contains("Reindexa 5"), ragIndex.Recommendation);
        var healthy = RagIndexInspector.Analyze([ragOk], ragStats, ragChanges, pageIndexed, "multilingual-e5-large", 1024);
        Assert(healthy.Findings.Count == 0 && healthy.Recommendation.StartsWith("Índice correcto"), healthy.Recommendation);
        Assert(RagIndexInspector.Analyze([ragOk], ragStats, null, null, "multilingual-e5-large", 1024, embeddingsEnabled: false)
            .Recommendation.Contains("desactivados"));
    }));
    await Check("Inspector RAG: un escaneado con fragmentos antiguos se marca como desfasado", () => Sync(() =>
    {
        var report = RagIndexInspector.Analyze([ragScanned], [new(ragScanned.Id, 1, "multilingual-e5-large", 1024, 4, indexedAt)], null, null, "multilingual-e5-large", 1024);
        Assert(report.Findings.Single().Kind == "rag-stale" && report.ReindexDocumentIds.Single() == ragScanned.Id);
    }));
}

if (args.Contains("--catalog"))
{
    var catalogDocument = new DocumentItem
    {
        InternalCode = "CAT-2026-0001", Title = "Título actual", Author = "Pruebas",
        Department = manager.Department, Category = "Contrato", Confidentiality = "Uso Interno",
        ExecutiveSummary = "Resumen actual.", Keywords = ["actual"]
    };
    var catalogDepartments = seed.Departments.Select(department => department.Name).ToList();
    await Check("Catalogador AG-01: interpreta una ficha válida", () => Sync(() =>
    {
        var json = """{"title":"Contrato de servicios 2026","category":"Contrato","department":"Tecnología & TI","confidentiality":"Confidencial","keywords":["SLA","2026"],"summary":"Resumen propuesto.","confidence":0.82}""";
        Assert(CatalogSuggester.TryParse(json, catalogDocument, manager, catalogDepartments, out var suggestion, out var error), error);
        Assert(suggestion.Title == "Contrato de servicios 2026" && suggestion.Category == "Contrato" &&
               suggestion.Department == "Tecnología & TI" && suggestion.Confidentiality == "Confidencial" &&
               suggestion.Keywords.SequenceEqual(["SLA", "2026"]) && suggestion.Confidence > 0.8);
    }));
    await Check("Catalogador AG-01: conserva valores si categoría, departamento o nivel no son válidos", () => Sync(() =>
    {
        var json = """{"title":"X","category":"Inexistente","department":"Inexistente","confidentiality":"Estrictamente Secreto","keywords":[],"summary":"","confidence":2}""";
        Assert(CatalogSuggester.TryParse(json, catalogDocument, manager, catalogDepartments, out var suggestion, out var error), error);
        Assert(suggestion.Category == catalogDocument.Category && suggestion.Department == catalogDocument.Department &&
               suggestion.Confidentiality == catalogDocument.Confidentiality && suggestion.Confidence <= 1 &&
               suggestion.Warnings.Count >= 3);
    }));
    await Check("Catalogador AG-01: limita palabras clave y resumen", () => Sync(() =>
    {
        var keywords = string.Join(",", Enumerable.Range(1, 12).Select(index => $"\"k{index}\""));
        var summary = new string('s', 1300);
        var json = $$"""{"title":"Título","category":"Contrato","department":"{{manager.Department}}","confidentiality":"Uso Interno","keywords":[{{keywords}}],"summary":"{{summary}}","confidence":0.5}""";
        Assert(CatalogSuggester.TryParse(json, catalogDocument, manager, catalogDepartments, out var suggestion, out var error), error);
        Assert(suggestion.Keywords.Count == 10 && suggestion.Summary.Length <= 1201 && suggestion.Warnings.Count >= 2);
    }));
    await Check("Catalogador AG-01: rechaza JSON inválido", () => Sync(() =>
    {
        Assert(!CatalogSuggester.TryParse("esto no es JSON", catalogDocument, manager, catalogDepartments, out _, out var error) &&
               error.Contains("JSON"));
    }));
    await Check("Catalogador AG-01: tolera el JSON envuelto en texto o bloque de código", () => Sync(() =>
    {
        var json = "```json\n{\"title\":\"Propuesta\",\"category\":\"Contrato\",\"department\":\"Tecnología & TI\",\"confidentiality\":\"Uso Interno\",\"keywords\":[\"k\"],\"summary\":\"s\",\"confidence\":0.1}\n```";
        Assert(CatalogSuggester.TryParse(json, catalogDocument, manager, catalogDepartments, out var suggestion, out var error), error);
        Assert(suggestion.Title == "Propuesta" && Math.Abs(suggestion.Confidence - 0.1) < 0.001);
    }));
}

if (args.Contains("--audit"))
{
    AuditEvent AuditRow(string action, string status, DateTimeOffset timestamp, string user = "tester") => new()
    {
        Action = action, Status = status, TimestampUTC = timestamp, UserName = user,
        UserRole = "gestor_documental", Details = "Evento de prueba"
    };
    var auditNow = DateTimeOffset.UtcNow;
    var auditEvents = new List<AuditEvent>();
    for (var index = 0; index < 5; index++) auditEvents.Add(AuditRow("LOGIN_FAILED", "DENEGADO", auditNow.AddDays(-1)));
    for (var index = 0; index < 3; index++) auditEvents.Add(AuditRow("ACCESS_DENIED", "DENEGADO", auditNow.AddDays(-2)));
    auditEvents.Add(AuditRow("INTEGRITY_MISMATCH", "ADVERTENCIA", auditNow.AddDays(-3)));
    auditEvents.Add(AuditRow("UPDATE_USER", "EXITOSO", auditNow.AddDays(-4)));
    auditEvents.Add(AuditRow("LOGIN", "EXITOSO", auditNow.AddDays(-5)));
    auditEvents.Add(AuditRow("DOWNLOAD", "EXITOSO", auditNow.AddDays(-20)));
    auditEvents.Add(AuditRow("DELETE", "EXITOSO", auditNow.AddDays(-25)));
    auditEvents.Add(AuditRow("DOWNLOAD", "EXITOSO", auditNow.AddDays(-60)));
    var auditReport = AuditInspector.Analyze(auditEvents, auditNow.AddDays(-30), auditNow);

    await Check("Auditor AG-05: cuenta eventos de riesgo del periodo", () => Sync(() =>
    {
        Assert(auditReport.TotalEvents == 13 && auditReport.FailedLogins == 5 &&
               auditReport.AccessDenied == 3 && auditReport.IntegrityMismatches == 1 &&
               auditReport.AdminChanges == 1 && auditReport.Downloads == 1 && auditReport.Deletions == 1);
    }));
    await Check("Auditor AG-05: genera alertas de integridad, fuerza bruta y accesos denegados", () => Sync(() =>
    {
        Assert(auditReport.Alerts.Any(alert => alert.Severity == "error" && alert.Title.Contains("Integridad")));
        Assert(auditReport.Alerts.Any(alert => alert.Severity == "warning" && alert.Title.Contains("inicio de sesión")));
        Assert(auditReport.Alerts.Any(alert => alert.Severity == "warning" && alert.Title.Contains("Accesos denegados")));
    }));
    await Check("Auditor AG-05: resume las acciones más frecuentes", () => Sync(() =>
    {
        Assert(auditReport.TopActions.Count > 0 && auditReport.TopActions[0].Action == "LOGIN_FAILED" &&
               auditReport.TopActions[0].Count == 5);
    }));
    await Check("Auditor AG-05: respeta el periodo consultado", () => Sync(() =>
    {
        var week = AuditInspector.Analyze(auditEvents, auditNow.AddDays(-7), auditNow);
        Assert(week.TotalEvents == 11 && week.Downloads == 0 && week.Deletions == 0);
    }));
    await Check("Auditor AG-05: informe vacío sin eventos", () => Sync(() =>
    {
        var empty = AuditInspector.Analyze([], auditNow.AddDays(-30), auditNow);
        Assert(empty.TotalEvents == 0 && empty.Alerts.Count == 0 && empty.TopActions.Count == 0);
    }));
}

if (args.Contains("--auth"))
{
    LoginAttemptLimiter NewLimiter() => new(new MemoryCache(new MemoryCacheOptions()),
        Options.Create(new AuthOptions { MaxFailedAttempts = 3, LockoutMinutes = 1 }));

    await Check("Bloqueo de login: funciona con MemoryCache SizeLimit", () => Sync(() =>
    {
        using var limitedCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10 });
        var limited = new LoginAttemptLimiter(limitedCache,
            Options.Create(new AuthOptions { MaxFailedAttempts = 3, LockoutMinutes = 1 }));
        limited.RegisterFailure("size-limit@example.test");
        limited.RegisterFailure("size-limit@example.test");
        limited.RegisterFailure("size-limit@example.test");
        Assert(limited.IsLocked("size-limit@example.test", out _));
    }));

    await Check("Bloqueo de login: tres fallos bloquean la cuenta", () => Sync(() =>
    {
        var limiter = NewLimiter();
        limiter.RegisterFailure("user@example.test");
        limiter.RegisterFailure("user@example.test");
        Assert(!limiter.IsLocked("user@example.test", out _));
        limiter.RegisterFailure("user@example.test");
        Assert(limiter.IsLocked("user@example.test", out var until) && until > DateTimeOffset.UtcNow);
    }));
    await Check("Bloqueo de login: dos fallos no bloquean", () => Sync(() =>
    {
        var limiter = NewLimiter();
        limiter.RegisterFailure("user2@example.test");
        limiter.RegisterFailure("user2@example.test");
        Assert(!limiter.IsLocked("user2@example.test", out _));
    }));
    await Check("Bloqueo de login: cuentas distintas no se contaminan", () => Sync(() =>
    {
        var limiter = NewLimiter();
        limiter.RegisterFailure("a@example.test");
        limiter.RegisterFailure("a@example.test");
        limiter.RegisterFailure("a@example.test");
        Assert(limiter.IsLocked("a@example.test", out _) && !limiter.IsLocked("b@example.test", out _));
    }));
    await Check("Bloqueo de login: un éxito reinicia el contador", () => Sync(() =>
    {
        var limiter = NewLimiter();
        limiter.RegisterFailure("reset@example.test");
        limiter.RegisterFailure("reset@example.test");
        limiter.Reset("reset@example.test");
        limiter.RegisterFailure("reset@example.test");
        Assert(!limiter.IsLocked("reset@example.test", out _));
    }));
    await Check("Bloqueo de login: correo vacío no bloquea", () => Sync(() =>
    {
        var limiter = NewLimiter();
        limiter.RegisterFailure("");
        limiter.RegisterFailure(null);
        Assert(!limiter.IsLocked("", out _) && !limiter.IsLocked(null, out _));
    }));
}

if (args.Contains("--rag"))
{
    var ragPersonas = new DocumentItem
    {
        InternalCode = "RAG-PERSONAS", Title = "Manual de personas", Category = "Manual Técnico",
        Department = "Tecnología & TI", Confidentiality = "Uso Interno",
        Pages = [new DocumentPage(1, "Las vacaciones se solicitan con quince días de antelación.", "Vacaciones y descanso.", [])]
    };
    ragPersonas.PageVectors[1] = [1f, 0f, 0f];
    var ragContrato = new DocumentItem
    {
        InternalCode = "RAG-CONTRATO", Title = "Contrato de servicios", Category = "Contrato",
        Department = "Tecnología & TI", Confidentiality = "Uso Interno",
        Pages = [new DocumentPage(1, "El proveedor garantiza mantenimiento preventivo y correctivo.", "Mantenimiento del servicio.", [])]
    };
    ragContrato.PageVectors[1] = [0f, 1f, 0f];
    var ragDocuments = new List<DocumentItem> { ragPersonas, ragContrato };

    await Check("RAG: el coseno ordena por similitud vectorial", () => Sync(() =>
    {
        Assert(Math.Abs(LocalSearch.Cosine([1f, 0f, 0f], [1f, 0f, 0f]) - 1f) < 0.0001f);
        Assert(Math.Abs(LocalSearch.Cosine([1f, 0f, 0f], [0f, 1f, 0f])) < 0.0001f);
        Assert(LocalSearch.Cosine([], []) == 0);
    }));
    await Check("RAG: recupera una página por significado aunque no coincidan términos", () => Sync(() =>
    {
        var hits = LocalSearch.Search(ragDocuments, "politica de descanso", minimum: 20, queryVector: [1f, 0f, 0f]);
        Assert(hits.Count > 0 && hits[0].Document.Id == ragPersonas.Id && hits[0].PageNumber == 1);
    }));
    await Check("RAG: un vector no relacionado no inventa resultados", () => Sync(() =>
    {
        var hits = LocalSearch.Search(ragDocuments, "consulta sin relacion", minimum: 20, queryVector: [0f, 0f, 1f]);
        Assert(hits.Count == 0);
    }));
    await Check("RAG: combina coincidencia léxica y vectorial sin perder la exacta", () => Sync(() =>
    {
        var hits = LocalSearch.Search(ragDocuments, "vacaciones", minimum: 0, queryVector: [0f, 1f, 0f]);
        Assert(hits.Count > 0 && hits.Any(hit => hit.Document.Id == ragPersonas.Id));
        var exact = LocalSearch.Search(ragDocuments, "vacaciones", exact: true, minimum: 0, queryVector: [0f, 1f, 0f]);
        Assert(exact.Count == 1 && exact[0].Document.Id == ragPersonas.Id);
    }));
    await Check("RAG: los vectores se serializan y recuperan sin pérdida", () => Sync(() =>
    {
        var vector = new[] { 0.25f, -0.5f, 0.75f };
        var restored = EmbeddingIndexer.FromBytes(EmbeddingIndexer.ToBytes(vector));
        Assert(restored.SequenceEqual(vector));
    }));

    // --- Pipeline por fragmentos (Services/Rag) ---
    RagDocument RagDoc(params string[] pages) => new("doc-1", "sql", "Documento", string.Join("\n", pages),
        pages.Select((text, index) => new RagPage(index + 1, text)).ToList(),
        new Dictionary<string, string> { ["code"] = "DOC-1" });
    IOptions<RagOptions> RagSettings(Action<RagOptions>? configure = null)
    {
        var value = new RagOptions { MaxTokens = 64, OverlapTokens = 16 };
        configure?.Invoke(value);
        return Options.Create(value);
    }
    string Words(int count, string prefix = "palabra") => string.Join(' ', Enumerable.Range(1, count).Select(i => $"{prefix}{i}"));
    int WordCount(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    void AssertConsecutive(IReadOnlyList<RagChunk> chunks)
    {
        Assert(chunks.Select(chunk => chunk.ChunkIndex).SequenceEqual(Enumerable.Range(0, chunks.Count)), "Índices no consecutivos");
        Assert(chunks.Select(chunk => chunk.Id).Distinct().Count() == chunks.Count, "IDs de fragmento repetidos");
    }
    RagChunk Chunk(int index, string text) => new($"doc-1:{index}", "doc-1", "DOC-1", "sql", 1, index, 0, text.Length, text, "");

    await Check("RAG chunks: el fijo respeta tamaño, offsets y numeración entre páginas", async () =>
    {
        var document = RagDoc(Words(200), Words(100, "otra"));
        var chunks = await new FixedRagChunker(RagSettings()).ChunkAsync(document);
        AssertConsecutive(chunks);
        Assert(chunks.All(chunk => WordCount(chunk.Text) <= 64));
        Assert(chunks.All(chunk => document.Pages[chunk.PageNumber - 1].Text[chunk.StartOffset..chunk.EndOffset] == chunk.Text));
        Assert(chunks.Any(chunk => chunk.PageNumber == 2));
        // Solape de 16 palabras: el fragmento 1 empieza en la palabra 49.
        Assert(chunks[1].Text.StartsWith("palabra49 "));
    });
    await Check("RAG chunks: el semántico no repite IDs cuando una página cae al fijo", async () =>
    {
        var embedder = new FakeRagEmbedder(_ => [1f, 0f]);
        var chunker = new SemanticRagChunker(RagSettings(), embedder, new FixedRagChunker(RagSettings()));
        var chunks = await chunker.ChunkAsync(RagDoc("Primera frase. Segunda frase.", ".....", "Tercera frase."));
        AssertConsecutive(chunks);
        Assert(chunks.Count == 3 && chunks[1].PageNumber == 2 && chunks[2].PageNumber == 3);
    });
    await Check("RAG chunks: el semántico parte oraciones mayores que MaxTokens", async () =>
    {
        var embedder = new FakeRagEmbedder(_ => [1f, 0f]);
        var chunker = new SemanticRagChunker(RagSettings(), embedder, new FixedRagChunker(RagSettings()));
        var chunks = await chunker.ChunkAsync(RagDoc(Words(150)));
        AssertConsecutive(chunks);
        Assert(chunks.Count == 3 && chunks.All(chunk => WordCount(chunk.Text) <= 64));
        Assert(chunks.Sum(chunk => WordCount(chunk.Text)) == 150);
    });
    await Check("RAG chunks: el semántico corta por cambio de tema y calcula embeddings por lote", async () =>
    {
        var embedder = new FakeRagEmbedder(text => text.Contains("gato") ? [1f, 0f] : [0f, 1f]);
        var chunker = new SemanticRagChunker(RagSettings(), embedder, new FixedRagChunker(RagSettings()));
        var chunks = await chunker.ChunkAsync(RagDoc("El gato duerme. El gato come. El perro ladra."));
        Assert(chunks.Count == 2 && chunks[1].Text == "El perro ladra.");
        Assert(embedder.BatchCalls == 1 && embedder.SingleCalls == 0);
    });
    await Check("RAG chunks: si fallan los embeddings el semántico usa el fijo sin romper", async () =>
    {
        var embedder = new FakeRagEmbedder(_ => throw new HttpRequestException("caído"));
        var chunker = new SemanticRagChunker(RagSettings(), embedder, new FixedRagChunker(RagSettings()));
        var chunks = await chunker.ChunkAsync(RagDoc(Words(100), "Otra página."));
        AssertConsecutive(chunks);
        Assert(chunks.Count == 3);
    });
    await Check("RAG BM25: el término raro pesa más que el presente en todos", () => Sync(() =>
    {
        string[] texts = ["comun raro", "comun", "comun otro"];
        var rare = HybridRagRetriever.Bm25Scores(["raro"], texts);
        var common = HybridRagRetriever.Bm25Scores(["comun"], texts);
        Assert(rare[0] > common[0], "IDF invertido");
        Assert(rare[1] == 0 && rare[2] == 0);
        var both = HybridRagRetriever.Bm25Scores(["comun", "raro"], texts);
        Assert(both[0] > both[1] && both[0] > both[2]);
        Assert(HybridRagRetriever.Bm25Scores([], texts).All(score => score == 0));
    }));
    await Check("RAG retriever: el reranker recibe el pool completo y el corte a TopK es al final", async () =>
    {
        var candidates = Enumerable.Range(0, 30)
            .Select(i => new RagScoredChunk(Chunk(i, $"texto {i}"), 1 - i * 0.01)).ToList();
        var reranker = new ReversingReranker();
        var retriever = new HybridRagRetriever(new FakeRagStore(candidates),
            RagSettings(o => { o.TopK = 8; o.RerankCandidates = 10; o.CandidateK = 30; }), reranker);
        var hits = await retriever.RetrieveAsync("consulta", [1f], ["doc-1"]);
        Assert(reranker.Received == 10, $"El reranker recibió {reranker.Received}");
        Assert(hits.Count == 8);
        // El décimo candidato de la fusión (índice 9) sube al primer puesto tras el reranking.
        Assert(hits[0].Chunk.ChunkIndex == 9);
    });
    await Check("RAG retriever: el pool de reranking nunca es menor que TopK", async () =>
    {
        var candidates = Enumerable.Range(0, 30)
            .Select(i => new RagScoredChunk(Chunk(i, $"texto {i}"), 1 - i * 0.01)).ToList();
        var reranker = new ReversingReranker();
        var retriever = new HybridRagRetriever(new FakeRagStore(candidates),
            RagSettings(o => { o.TopK = 8; o.RerankCandidates = 3; }), reranker);
        var hits = await retriever.RetrieveAsync("consulta", [1f], ["doc-1"]);
        Assert(reranker.Received == 8 && hits.Count == 8);
        Assert(HybridRagRetriever.RerankPoolSize(100, 8) == CrossEncoderRagReranker.MaxCandidates);
    });
    await Check("RAG reranker: los logits se normalizan a [0, 1] conservando el orden", () => Sync(() =>
    {
        var logits = CrossEncoderRagReranker.NormalizeScores([4.1, -10.8, 0.3]);
        Assert(logits.All(score => score is >= 0 and <= 1));
        Assert(logits[0] > logits[2] && logits[2] > logits[1]);
        var probabilities = CrossEncoderRagReranker.NormalizeScores([0.2, 0.9]);
        Assert(probabilities.SequenceEqual([0.2, 0.9]), "Los scores ya normalizados no deben cambiar");
        Assert(CrossEncoderRagReranker.NormalizeScores([double.NaN, 1.5])[0] == 0);
    }));
    await Check("RAG ingesta: reemplaza de forma atómica, guarda el modelo y vacía si no hay texto", async () =>
    {
        var store = new RecordingRagStore();
        var embedder = new FakeRagEmbedder(_ => [1f, 0f]);
        var ingestion = new RagIngestionService(RagSettings(), new FixedRagChunker(RagSettings()),
            new SemanticRagChunker(RagSettings(), embedder, new FixedRagChunker(RagSettings())),
            embedder, store, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<RagIngestionService>.Instance);
        var result = await ingestion.IngestAsync([RagDoc(Words(100))]);
        Assert(result == (1, 2) && store.Replaced.Count == 1);
        Assert(store.Replaced[0].Chunks.All(item => item.Model == "fake"));
        await ingestion.IngestAsync([RagDoc("   ")]);
        Assert(store.Replaced.Count == 2 && store.Replaced[1].DocumentId == "doc-1" && store.Replaced[1].Chunks.Count == 0,
            "Un documento sin texto debe dejar el índice vacío, no conservar fragmentos viejos");
    });
    await Check("RAG store: ignora fragmentos de otro modelo o dimensiones", () => Sync(() =>
    {
        var record = new RagChunkRecord { Model = "multilingual-e5-large", Dimensions = 1024 };
        Assert(SqlRagVectorStore.IsCompatible(record, 1024, "multilingual-e5-large"));
        Assert(SqlRagVectorStore.IsCompatible(record, 1024, "MULTILINGUAL-E5-LARGE"));
        Assert(!SqlRagVectorStore.IsCompatible(record, 768, "multilingual-e5-large"));
        Assert(!SqlRagVectorStore.IsCompatible(record, 1024, "nomic-embed-text"));
        Assert(SqlRagVectorStore.IsCompatible(new RagChunkRecord { Model = "", Dimensions = 1024 }, 1024, "multilingual-e5-large"),
            "Los registros anteriores sin modelo se aceptan hasta reindexar");
    }));
    await Check("RAG embedder: la caché LRU desaloja el menos usado", () => Sync(() =>
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert(cache.TryGet("a", out _));
        cache.Set("c", 3);
        Assert(cache.Count == 2 && cache.TryGet("a", out var a) && a == 1 && cache.TryGet("c", out _));
        Assert(!cache.TryGet("b", out _), "Debía desalojarse «b»");
    }));
    await Check("Pinecone: borra por prefijo recorriendo todas las páginas del listado", async () =>
    {
        var deleted = new List<string>();
        var listed = new List<string>();
        var handler = new TestHttpHandler(async (request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            string body;
            if (path.EndsWith("/indexes/rag", StringComparison.Ordinal))
                body = """{"dimension":2,"metric":"cosine","host":"idx.test","status":{"ready":true}}""";
            else if (path == "/vectors/list")
            {
                var query = request.RequestUri.Query;
                listed.Add(query);
                body = query.Contains("paginationToken=t2")
                    ? """{"vectors":[{"id":"rag:doc-1:2"}]}"""
                    : """{"vectors":[{"id":"rag:doc-1:0"},{"id":"rag:doc-1:1"}],"pagination":{"next":"t2"}}""";
            }
            else if (path == "/vectors/delete")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                deleted.AddRange(json.RootElement.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!));
                body = "";
            }
            else return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var client = new PineconeClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") },
            Options.Create(new PineconeOptions { Enabled = true, ApiKey = "k", IndexName = "rag", Dimension = 2 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PineconeClient>.Instance);
        var count = await client.DeleteByPrefixAsync(PineconeRagVectorStore.DocumentPrefix("doc-1"));
        Assert(count == 3 && deleted.SequenceEqual(["rag:doc-1:0", "rag:doc-1:1", "rag:doc-1:2"]));
        Assert(listed.Count == 2 && listed.All(query => Uri.UnescapeDataString(query).Contains("prefix=rag:doc-1:")));
        await ThrowsAsync<ArgumentException>(() => client.DeleteByPrefixAsync(""));
    });
    await Check("RAG eval: detecta preguntas que revelan el código o copian el texto", () => Sync(() =>
    {
        const string reference = "Los colaboradores podrán optar por el esquema de 3 días presenciales en oficina";
        Assert(RagEvaluationService.LeakReason("¿Qué dice RH-2025-REG-01?", "RH-2025-REG-01", reference) is not null);
        Assert(RagEvaluationService.LeakReason(
            "Que se dice sobre: podrán optar por el esquema de 3 días?", "X", reference) is not null);
        Assert(RagEvaluationService.LeakReason("¿Cuántos días se puede trabajar desde casa?", "X", reference) is null);
    }));
    await Check("RAG eval: el acierto es por documento y página, con chunk opcional y rango para MRR", () => Sync(() =>
    {
        RagCitation Source(string code, int page, int chunk) => new(code, "t", page, chunk, 0.5, "");
        IReadOnlyList<RagCitation> sources = [Source("A", 1, 0), Source("B", 2, 7), Source("B", 3, 8)];
        Assert(RagEvaluationService.FirstMatchRank(sources, new RagEvalCase("1", "q", "b", "B", 2)) == 2);
        Assert(RagEvaluationService.FirstMatchRank(sources, new RagEvalCase("2", "q", "b", "b", 3, 8)) == 3);
        Assert(RagEvaluationService.FirstMatchRank(sources, new RagEvalCase("3", "q", "b", "B", 3, 1)) == 0);
        Assert(RagEvaluationService.FirstMatchRank(sources, new RagEvalCase("4", "q", "c", "C", 1)) == 0);
    }));
    await Check("RAG eval: el set de preguntas es válido, cubre el seed y no filtra la respuesta", async () =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eval", "rag_questions_seed.json")))
            directory = directory.Parent;
        Assert(directory is not null, "No se encontró eval/rag_questions_seed.json");
        var cases = JsonSerializer.Deserialize<List<RagEvalCase>>(
            await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "eval", "rag_questions_seed.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert(cases.Count >= 10 && cases.Select(item => item.Id).Distinct().Count() == cases.Count);
        var pages = seed.Documents.SelectMany(document => document.Pages.Select(page => (document, page))).ToList();
        foreach (var item in cases)
        {
            var match = pages.FirstOrDefault(pair => pair.document.Id == item.ExpectedDocumentId && pair.page.PageNumber == item.ExpectedPage);
            Assert(match.document is not null, $"{item.Id}: página inexistente en el seed");
            Assert(match.document!.InternalCode == item.ExpectedCode, $"{item.Id}: código no coincide");
            Assert(RagEvaluationService.LeakReason(item.Question, item.ExpectedCode, match.page.ExtractedText) is null,
                $"{item.Id}: la pregunta revela la respuesta");
        }
        Assert(cases.Select(item => item.ExpectedDocumentId).Distinct().Count() == seed.Documents.Count,
            "El set debe cubrir todos los documentos del seed");
    });

    RagQueryResponse Response(string outcome, long latency, params (string Code, double Score)[] sources) =>
        new(new string('r', 3000), sources.Select((source, index) => new RagCitation(source.Code, "t", 1, index, source.Score, "")).ToList(),
            outcome == RagOutcome.NoCoverage ? RagQueryService.NoCoverageEngine : "OpenCode", latency, outcome);
    var observedNow = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    AuditEvent RagEvent(string question, RagQueryResponse response, int minutesAgo, bool content = true) => new()
    {
        Action = "RAG_QUERY", TimestampUTC = observedNow.AddMinutes(-minutesAgo),
        Details = RagQueryService.AuditDetails(question, response, content)
    };

    await Check("RAG auditoría: sin AuditContent no guarda la pregunta ni la respuesta", () => Sync(() =>
    {
        var response = Response(RagOutcome.Generated, 120, ("DOC-A", 0.8));
        var hidden = RagQueryService.AuditDetails("¿Cuál es el salario confidencial?", response, auditContent: false);
        Assert(!hidden.Contains("salario") && !hidden.Contains("rrr") && hidden.Contains("\"queryHash\""), hidden);
        Assert(hidden.Contains("\"outcome\":\"generated\"") && hidden.Contains("\"topScore\":0.8"), hidden);
        var visible = RagQueryService.AuditDetails("¿Cuál es el salario?", response, auditContent: true);
        using var json = JsonDocument.Parse(visible);
        Assert(json.RootElement.GetProperty("query").GetString() == "¿Cuál es el salario?");
        Assert(json.RootElement.GetProperty("answer").GetString()!.Length <= RagQueryService.AuditAnswerChars + 1);
    }));
    await Check("RAG auditoría: el hash agrupa la misma pregunta con otra puntuación, mayúsculas o tildes", () => Sync(() =>
    {
        Assert(RagQueryService.QueryHash("¿Qué es la Acreditación?") == RagQueryService.QueryHash("que es la acreditacion"));
        Assert(RagQueryService.QueryHash("¿Qué es la acreditación?") != RagQueryService.QueryHash("¿Qué es la evaluación?"));
    }));

    var observedEvents = new List<AuditEvent>
    {
        RagEvent("¿Dónde está el manual de vacaciones?", Response(RagOutcome.NoCoverage, 40), 10),
        RagEvent("donde esta el manual de vacaciones", Response(RagOutcome.NoCoverage, 50), 20),
        RagEvent("¿Dónde está el MANUAL de vacaciones?", Response(RagOutcome.NoCoverage, 60), 30, content: false),
        RagEvent("¿Cuál es la política de viajes?", Response(RagOutcome.Generated, 900, ("DOC-A", 0.15)), 40),
        RagEvent("¿Qué dice el contrato?", Response(RagOutcome.Generated, 1000, ("DOC-A", 0.9), ("DOC-SECRETO", 0.7)), 50),
        RagEvent("¿Qué dice el informe?", Response(RagOutcome.EngineFallback, 300, ("DOC-B", 0.8)), 60),
        new() { Action = "RAG_QUERY", TimestampUTC = observedNow.AddMinutes(-70), Details = "{no es json" },
        new()
        {
            Action = "RAG_QUERY", TimestampUTC = observedNow.AddMinutes(-80),
            Details = """{"query":"antigua","answer":"x","engine":"extractivo · verificación AG-02","latencyMs":200,"sources":[{"documentCode":"DOC-B","pageNumber":1,"chunkIndex":0,"score":0.6}]}"""
        },
        RagEvent("fuera del periodo", Response(RagOutcome.Generated, 10, ("DOC-A", 0.9)), 60 * 24 * 10)
    };
    var readableCodes = new[] { "DOC-A", "DOC-B", "DOC-C" };
    var observed = RagObservatory.Analyze(observedEvents, readableCodes, observedNow.AddDays(-7), observedNow, showQuestions: true);

    await Check("RAG Observador: clasifica resultados, ignora JSON roto y entiende eventos antiguos", () => Sync(() =>
    {
        Assert(observed.Queries == 7 && observed.Ignored == 1, $"{observed.Queries}/{observed.Ignored}");
        Assert(observed.Count(RagOutcome.NoCoverage) == 3 && observed.Count(RagOutcome.Generated) == 2);
        Assert(observed.Count(RagOutcome.EngineFallback) == 1 && observed.Count(RagOutcome.VerificationFailed) == 1);
        Assert(observed.LowConfidence == 1 && Math.Abs(observed.Rate(RagOutcome.NoCoverage) - 3.0 / 7) < 1e-9);
    }));
    await Check("RAG Observador: agrupa las preguntas sin cobertura aunque una no guarde texto", () => Sync(() =>
    {
        Assert(observed.GapQuestionsTotal == 2, observed.GapQuestionsTotal.ToString());
        var top = observed.Gaps[0];
        Assert(top.Count == 3 && top.Reason == "sin cobertura" && top.Question == "¿Dónde está el manual de vacaciones?", top.ToString());
        Assert(observed.Gaps[1].Reason == "baja confianza");
        var hidden = RagObservatory.Analyze(observedEvents, readableCodes, observedNow.AddDays(-7), observedNow, showQuestions: false);
        Assert(hidden.Gaps.All(gap => gap.Question is null) && !hidden.QuestionsVisible && hidden.GapQuestionsTotal == 2);
    }));
    await Check("RAG Observador: uso de expedientes solo con códigos legibles", () => Sync(() =>
    {
        Assert(observed.TopDocuments[0] == new RagDocumentUsage("DOC-A", 2), observed.TopDocuments[0].ToString());
        Assert(observed.TopDocuments.All(usage => usage.DocumentCode != "DOC-SECRETO"));
        Assert(observed.NeverCited.SequenceEqual(["DOC-C"]) && observed.NeverCitedTotal == 1);
    }));
    await Check("RAG Observador: latencia p50/p95 por motor y serie diaria", () => Sync(() =>
    {
        var opencode = observed.Latency.Single(engine => engine.Engine == "OpenCode");
        Assert(opencode.Queries == 3 && opencode.P50Ms == 900 && opencode.P95Ms == 1000, opencode.ToString());
        Assert(observed.Daily.Sum(day => day.Queries) == 7 && observed.Daily.Sum(day => day.NoCoverage) == 3);
    }));
    await Check("Chat: el formato extractivo se convierte en fragmentos citados y aviso", () => Sync(() =>
    {
        var blocks = AnswerFormatter.Format("Primer fragmento\ncon dos líneas\n[CNT-2026-ABC123, p. 2]\n\nOtro texto\n[POL-2025-0012, p. 1]\n\nRespuesta extractiva con citas verificables.");
        Assert(blocks.Count == 3 && blocks[0] is AnswerExcerpt { Code: "CNT-2026-ABC123", Page: 2 } first &&
            string.Concat(first.Content.Select(part => part.Text)) == "Primer fragmento con dos líneas", string.Join(" | ", blocks));
        Assert(blocks[1] is AnswerExcerpt { Code: "POL-2025-0012", Page: 1 } && blocks[2] is AnswerNote);
    }));
    await Check("Chat: Markdown del modelo a encabezado, lista con negrita y cita, y tabla", () => Sync(() =>
    {
        var blocks = AnswerFormatter.Format("## Resumen\n- **Plazo**: 30 días [1]\n- Sin penalización\n\n| Concepto | Monto |\n|---|---:|\n| Licencia | 500 € |\n| Soporte | 200 € |");
        Assert(blocks[0] is AnswerHeading && blocks[1] is AnswerList { Ordered: false, Items.Count: 2 } list &&
            list.Items[0].Any(part => part.Kind == AnswerInlineKind.Bold && part.Text == "Plazo") &&
            list.Items[0].Any(part => part.Kind == AnswerInlineKind.Citation && part.Index == 1), string.Join(" | ", blocks));
        Assert(blocks[2] is AnswerTable { Headers.Count: 2, Rows.Count: 2 } table && table.Rows[1][1][0].Text == "200 €");
    }));
    await Check("Chat: entidades, listados y fichas se tabulan", () => Sync(() =>
    {
        var entities = AnswerFormatter.Format("MONEY: 500 € — [FIN-2024-Q4-01, p. 3]\nDATE: 2024 — [FIN-2024-Q4-01, p. 1]");
        Assert(entities.Single() is AnswerTable { Rows.Count: 2 } money && money.Headers.SequenceEqual(["Tipo", "Valor", "Fuente"]) &&
            money.Rows[0][0][0].Text == "Monto" && money.Rows[0][2][0] is { Kind: AnswerInlineKind.Citation, Code: "FIN-2024-Q4-01", Page: 3 });
        var listing = AnswerFormatter.Format("2 expediente(s) accesible(s).\nCNT-2026-AAA — Contrato marco (Uso Interno)\nPOL-2025-0012 — Política (Público)");
        Assert(listing.Count == 2 && listing[0] is AnswerParagraph && listing[1] is AnswerTable { Rows.Count: 2, Headers.Count: 3 }, string.Join(" | ", listing));
        var card = AnswerFormatter.Format("CNT-2026-AAA — Contrato marco\nAutor: Ana\nDepartamento: Legal\n\nResumen del contrato.");
        Assert(card.Count == 3 && card[0] is AnswerHeading && card[1] is AnswerTable { Rows.Count: 2 } && card[2] is AnswerParagraph, string.Join(" | ", card));
    }));
    LocalSession MemorySession(string userId, string sessionId)
    {
        var session = new LocalSession();
        session.SignIn(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(AuthService.SessionClaim, sessionId)], "checks")));
        return session;
    }
    MemoryTurn Turn(bool user, string text, params MemorySource[] sources) => new(user, text, "test", false, DateTimeOffset.UtcNow, sources);

    await Check("Memoria: la conversación sobrevive entre páginas y caduca a las 24 h", () => Sync(() =>
    {
        var clock = new ManualClock { Now = DateTimeOffset.UtcNow };
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var memory = new AssistantMemory(cache, clock);
        var session = MemorySession("usr-a", "login-1");
        var expires = memory.AddTurn(session, Turn(true, "¿Qué plazo tiene el contrato?"));
        memory.AddTurn(session, Turn(false, "30 días", new MemorySource("doc-1", 2, "fragmento", 80)));
        memory.SetFocus(session, "CNT-2026-AAA");
        Assert(expires == clock.Now + AssistantMemory.Lifetime);
        // Otra instancia del componente (cambio de página) lee la misma conversación.
        var restored = memory.GetConversation(MemorySession("usr-a", "login-1"));
        Assert(restored.Turns.Count == 2 && restored.FocusDocumentCode == "CNT-2026-AAA" && restored.Turns[1].Sources[0].DocumentId == "doc-1");
        clock.Now = clock.Now.AddHours(23);
        Assert(memory.GetConversation(session).Turns.Count == 2, "Antes de 24 h sigue disponible");
        clock.Now = clock.Now.AddHours(1).AddMinutes(1);
        Assert(memory.GetConversation(session).Turns.Count == 0 && memory.GetConversation(session).ExpiresUtc is null, "A las 24 h se borra");
    }));
    await Check("Memoria: aislada por usuario e inicio de sesión; se borra al cerrar sesión o a mano", () => Sync(() =>
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var memory = new AssistantMemory(cache);
        var first = MemorySession("usr-a", "login-1");
        memory.AddTurn(first, Turn(true, "pregunta"));
        memory.SaveSearch(first, new SearchMemory("ISO 27001", false, 10, "", "", "", "", ["ISO 27001", "ISO 27001", "SLA"]));
        Assert(memory.GetConversation(MemorySession("usr-b", "login-1")).Turns.Count == 0, "Otro usuario no la ve");
        Assert(memory.GetConversation(MemorySession("usr-a", "login-2")).Turns.Count == 0, "Un nuevo inicio de sesión empieza vacío");
        Assert(memory.GetSearch(first)!.History.SequenceEqual(["ISO 27001", "SLA"]));
        memory.ClearConversation(first);
        Assert(memory.GetConversation(first).Turns.Count == 0 && memory.GetSearch(first) is not null, "«Nueva conversación» no borra el buscador");
        memory.AddTurn(first, Turn(true, "otra"));
        memory.ClearSession("usr-a", "login-1");
        Assert(memory.GetConversation(first).Turns.Count == 0 && memory.GetSearch(first) is null, "El cierre de sesión borra todo");
        Throws<UnauthorizedAccessException>(() => memory.GetConversation(new LocalSession()));
    }));
    await Check("Memoria: limita el número de turnos guardados", () => Sync(() =>
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var memory = new AssistantMemory(cache);
        var session = MemorySession("usr-a", "login-1");
        for (var i = 0; i < AssistantMemory.MaxTurns + 5; i++) memory.AddTurn(session, Turn(true, $"p{i}"));
        var turns = memory.GetConversation(session).Turns;
        Assert(turns.Count == AssistantMemory.MaxTurns && turns[0].Text == "p5");
    }));
    await Check("Memoria conversacional: completa las preguntas de seguimiento", () => Sync(() =>
    {
        Assert(ConversationContext.IsFollowUp("¿Y el plazo?"));
        Assert(ConversationContext.IsFollowUp("Resume ese documento por favor"));
        Assert(!ConversationContext.IsFollowUp("¿Qué contratos tienen penalizaciones por incumplimiento del SLA?"));
        Assert(!ConversationContext.IsFollowUp("¿Qué norma está vigente para el teletrabajo en la empresa?"), "«está» no es demostrativo");
        Assert(!ConversationContext.IsFollowUp("Resume ese documento CNT-2026-AAA"), "Si nombra un expediente es autónoma");
        Assert(!ConversationContext.IsFollowUp("Fabic"), "Un tema corto nuevo no debe heredar la conversación anterior");
        Assert(!ConversationContext.IsFollowUp("¿Qué es Fabic?"), "Una pregunta de definición es autónoma");
        var corrected = QueryCorrector.Correct("¿Qué es Fabic?", [new DocumentItem
        {
            Title = "Estrategia Ejecutiva de Microsoft Fabric", Keywords = ["Fabric", "datos"]
        }]);
        Assert(corrected.Contains("fabric", StringComparison.OrdinalIgnoreCase) && !corrected.Contains("fabic", StringComparison.OrdinalIgnoreCase),
            corrected);
        IReadOnlyList<MemoryTurn> history = [Turn(true, "¿Qué plazo de preaviso fija el contrato marco?"), Turn(false, "30 días")];
        var hint = ConversationContext.Build("¿Y la penalización?", history, "CNT-2026-AAA");
        Assert(hint.IsFollowUp && hint.RetrievalQuery.StartsWith("¿Qué plazo de preaviso") && hint.RetrievalQuery.EndsWith("¿Y la penalización?"));
        Assert(hint.GenerationQuestion.StartsWith("¿Y la penalización?") && hint.GenerationQuestion.Contains("CNT-2026-AAA") && hint.FocusDocumentCode == "CNT-2026-AAA");
        var standalone = ConversationContext.Build("¿Y la penalización?", [], "CNT-2026-AAA");
        Assert(!standalone.IsFollowUp && standalone.RetrievalQuery == "¿Y la penalización?" && standalone.FocusDocumentCode is null);
    }));
    await Check("Síntesis: detecta resumen, análisis, varios documentos y preguntas sin tema", () => Sync(() =>
    {
        var bare = DocumentSynthesis.Detect("Resumen");
        Assert(bare.Style == AnswerStyle.Summary && bare.Bare && !bare.MultiDocument);
        var contracts = DocumentSynthesis.Detect("Hazme un análisis de los contratos de nube");
        Assert(contracts.Style == AnswerStyle.Analysis && contracts.MultiDocument && !contracts.Bare);
        var named = DocumentSynthesis.Detect("Resume CNT-2026-AAA y POL-2025-0012");
        Assert(named.Style == AnswerStyle.Summary && named.MultiDocument && named.Codes.SequenceEqual(["CNT-2026-AAA", "POL-2025-0012"]) && !named.Bare);
        var related = DocumentSynthesis.Detect("resume ese documento y sus relacionados");
        Assert(related.Style == AnswerStyle.Summary && related.Bare && related.WantsRelated);
        Assert(DocumentSynthesis.Detect("Compara las políticas de teletrabajo") is { Style: AnswerStyle.Analysis, MultiDocument: true, Bare: false });
        Assert(DocumentSynthesis.Detect("¿Qué plazo de preaviso tiene el contrato?").Style == AnswerStyle.Default);
        Assert(DocumentSynthesis.Detect("¿Qué prueba estadística se empleó para comparar las medias de las variables estudiadas?").Style == AnswerStyle.Default,
            "Un verbo en mitad de una pregunta concreta no la convierte en análisis");
        Assert(DocumentSynthesis.Detect("¿Qué tienen en común los contratos?").Style == AnswerStyle.Analysis);
        Assert(DocumentSynthesis.Detect("¿Me puedes resumir las políticas de seguridad?").Style == AnswerStyle.Summary);
        Assert(DocumentSynthesis.Detect("¿Qué diferencias hay entre los contratos? Explica las diferencias entre ambos").Style == AnswerStyle.Analysis);
        Assert(DocumentSynthesis.Detect("¿Qué variable mostró diferencias significativas en los puntajes?").Style == AnswerStyle.Default);
        var topic = DocumentSynthesis.Detect("puedes generar un resumen completo de Mesopotamia");
        Assert(topic.Style == AnswerStyle.Summary && !topic.Bare);
        Assert(DocumentSynthesis.Subject("puedes generar un resumen completo de Mesopotamia") == "mesopotamia");
    }));

    DocumentItem SynthesisDoc(string code, string category, string[] keywords, params string[] pages) => new()
    {
        InternalCode = code, Title = $"Título {code}", Author = "Pruebas", Department = "Legal", Category = category,
        Confidentiality = "Uso Interno", ExecutiveSummary = $"El documento {code} regula el servicio. Fija plazos y responsables. Incluye anexos.",
        Keywords = keywords, PageCount = pages.Length, UploadDate = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
        Pages = pages.Select((text, index) => new DocumentPage(index + 1, text, text.Split('.')[0] + ".",
            index == 0 ? [new DocumentEntity("e1", "500 €", "MONEY", 1)] : [])).ToList()
    };
    var synthA = SynthesisDoc("CNT-2026-AAA", "Contrato", ["nube", "sla", "soporte"],
        "Contrato de nube. Disponibilidad del 99,9 %.", "Penalizaciones por incumplimiento del SLA.", "Soporte 24x7 con respuesta en 4 horas.");
    var synthB = SynthesisDoc("POL-2025-0012", "Política & Norma", ["nube", "seguridad", "sla"],
        "Política de uso de la nube.", "Clasificación de datos.", "Copias de seguridad diarias.", "Revisión anual de la política.");
    RagHit SynthesisHit(DocumentItem document, int page, double score) =>
        new(new RagChunk($"{document.Id}:{page}", document.Id, document.InternalCode, "sql", page, page - 1, 0, 10,
            document.Pages[page - 1].ExtractedText, "h"), score);

    await Check("Síntesis: las preguntas concretas de los sets de evaluación siguen la vía normal", async () =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "eval"))) directory = directory.Parent;
        var files = Directory.GetFiles(Path.Combine(directory!.FullName, "eval"), "rag_questions*.json");
        Assert(files.Length >= 2);
        foreach (var file in files)
            foreach (var item in JsonSerializer.Deserialize<List<RagEvalCase>>(await File.ReadAllTextAsync(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!)
                Assert(DocumentSynthesis.Detect(item.Question).Style == AnswerStyle.Default, $"{Path.GetFileName(file)} {item.Id}: {item.Question}");
    });
    await Check("Síntesis: elige los documentos relacionados por cercanía al mejor resultado", () => Sync(() =>
    {
        var hits = new List<RagHit> { SynthesisHit(synthA, 2, 0.9), SynthesisHit(synthB, 1, 0.7), SynthesisHit(synthA, 3, 0.5),
            new(new RagChunk("x:1", "doc-lejano", "X", "sql", 1, 0, 0, 1, "t", "h"), 0.3) };
        Assert(DocumentSynthesis.RankDocuments(hits).SequenceEqual([synthA.Id, synthB.Id]));
        Assert(DocumentSynthesis.RankDocuments(hits, max: 1).SequenceEqual([synthA.Id]));
        Assert(DocumentSynthesis.RankDocuments(hits, relative: 0.8).SequenceEqual([synthA.Id]));
        Assert(DocumentSynthesis.RankDocuments([]).Count == 0);
    }));
    await Check("Síntesis: reparte hasta 6 páginas distintas entre documentos y el Verificador las acepta", () => Sync(() =>
    {
        var byDocument = new Dictionary<string, IReadOnlyList<RagHit>>
        {
            [synthA.Id] = [SynthesisHit(synthA, 2, 0.9), SynthesisHit(synthA, 2, 0.8), SynthesisHit(synthA, 3, 0.6)]
        };
        var sources = DocumentSynthesis.SelectSources([synthA, synthB], byDocument, max: 6);
        Assert(DocumentSynthesis.MaxSources == RagContextLimits.MaxSynthesisSources);
        Assert(sources.Count == 6 && sources.DistinctBy(source => (source.Document.Id, source.PageNumber)).Count() == 6);
        Assert(sources[0].Document == synthA && sources[0].PageNumber == 2 && sources[1].Document == synthB && sources[1].PageNumber == 1,
            "Turnos: primero la página donde se encontró la consulta, luego la primera página del otro documento");
        Assert(sources.Count(source => source.Document == synthA) == 3 && sources.Count(source => source.Document == synthB) == 3);
        Assert(AgentVerifier.Verify(new AgentAnswer("Resumen [1]", sources), [synthA, synthB], admin).IsValid);
        Assert(DocumentSynthesis.SelectSources([SynthesisDoc("SCN-2026-001", "Contrato", [], "  ")], byDocument).Count == 0,
            "Un escaneado sin texto no aporta fuentes");
    }));
    await Check("Síntesis: resumen extractivo de varios documentos en lenguaje natural", () => Sync(() =>
    {
        var sources = DocumentSynthesis.SelectSources([synthA, synthB], new Dictionary<string, IReadOnlyList<RagHit>>());
        var text = DocumentSynthesis.Compose(AnswerStyle.Summary, [synthA, synthB], sources);
        Assert(text.StartsWith("He revisado 2 documentos relacionados con tu consulta: **CNT-2026-AAA** y **POL-2025-0012**."), text);
        Assert(text.Contains("**En común:** tratan temas compartidos como nube y sla.") && text.Contains("[POL-2025-0012, p. 1]"), text);
        var blocks = AnswerFormatter.Format(text);
        Assert(blocks.OfType<AnswerParagraph>().Count() == 4 && blocks[^1] is AnswerNote, string.Join(" | ", blocks));
        Assert(blocks.OfType<AnswerParagraph>().Any(paragraph => paragraph.Content.Any(part => part is { Kind: AnswerInlineKind.Citation, Code: "CNT-2026-AAA" })));
    }));
    await Check("Síntesis: análisis con diferencias y datos a tener en cuenta; resumen de un solo documento", () => Sync(() =>
    {
        var analysis = DocumentSynthesis.Compose(AnswerStyle.Analysis, [synthA, synthB], []);
        Assert(analysis.Contains("**Diferencias:** CNT-2026-AAA es contrato; POL-2025-0012 es política & norma.") &&
            analysis.Contains("**A tener en cuenta:** en total mencionan 2 importe(s)"), analysis);
        var single = DocumentSynthesis.Compose(AnswerStyle.Summary, [synthA], []);
        Assert(single.StartsWith("**CNT-2026-AAA · Título CNT-2026-AAA** es un contrato del área de Legal, de 3 página(s).") &&
            !single.Contains("En común"), single);
        var overview = DocumentSynthesis.Compose(AnswerStyle.Summary, [synthA, synthB], [], totalAvailable: 9);
        Assert(overview.StartsWith("Tienes acceso a 9 expedientes. Este es un resumen de los 2 más relevantes"), overview);
        Assert(DocumentSynthesis.Gist(new DocumentItem()).Contains("OCR"));
        Assert(DocumentSynthesis.Gist(new DocumentItem { ExecutiveSummary = string.Join(" ", Enumerable.Repeat("Frase larga de prueba sin fin.", 40)) }).Length
            <= DocumentSynthesis.MaxGistChars + 1);
    }));
    await Check("PDF: una respuesta con fuentes genera un documento PDF válido", () => Sync(() =>
    {
        var document = new DocumentItem
        {
            InternalCode = "PDF-2026-001",
            Title = "Documento de prueba",
            PageCount = 1,
            Pages = [new DocumentPage(1, "Texto de prueba", "Resumen", [])]
        };
        var pdf = RagPdfExporter.Build("resumen de Mesopotamia", "**Resumen** con [1] y acentos: ñ á é.",
            [new SearchHit(document, 1, "Texto de prueba", 90)]);
        Assert(Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-");
        using var parsed = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert(parsed.NumberOfPages >= 1);
    }));
    await Check("Síntesis: el prompt pide lenguaje natural y cabe en el límite de los workers", () => Sync(() =>
    {
        foreach (var style in Enum.GetValues<AnswerStyle>()) Assert(AssistantPrompt.For(style).Length < 4000, style.ToString());
        Assert(AssistantPrompt.For(AnswerStyle.Summary).Contains("Resumen general") && AssistantPrompt.For(AnswerStyle.Analysis).Contains("Comparación"));
        Assert(AssistantPrompt.For(AnswerStyle.Default).Contains("completa y bien explicada") &&
            AssistantPrompt.For(AnswerStyle.Default).Contains("Límites de las fuentes") &&
            AssistantPrompt.For(AnswerStyle.Default).StartsWith(AssistantPrompt.System));
    }));
    await Check("Contexto: fusiona los fragmentos de una página sin repetir el solape y el Verificador lo acepta", () => Sync(() =>
    {
        const string page = "Uno dos tres cuatro cinco. Seis siete ocho nueve diez. Once doce trece catorce quince.";
        var doc = SynthesisDoc("CTX-2026-001", "Contrato", [], page, "Otra página distinta.");
        doc.Id = "doc-ctx";
        RagChunk Piece(int index, int start, int end) =>
            new($"doc-ctx:{index}", "doc-ctx", doc.InternalCode, "sql", 1, index, start, end, page[start..end], $"h{index}");
        var a = Piece(0, 0, page.IndexOf("ocho", StringComparison.Ordinal) + 4);           // «Uno … ocho»
        var b = Piece(1, page.IndexOf("Seis", StringComparison.Ordinal), page.IndexOf("diez", StringComparison.Ordinal) + 4); // solapa con a
        var c = Piece(2, page.IndexOf("Once", StringComparison.Ordinal), page.Length);     // no contiguo con b
        var other = new RagChunk("doc-ctx:3", "doc-ctx", doc.InternalCode, "sql", 2, 3, 0, 21, "Otra página distinta.", "h3");
        var merged = RagContext.MergeByPage([new(c, 0.5), new(other, 0.7), new(a, 0.9), new(b, 0.4)]);
        Assert(merged.Count == 2 && merged[0].First.PageNumber == 1 && merged[0].ChunkCount == 3 && merged[0].Score == 0.9,
            "Una fuente por página, ordenada por su mejor fragmento");
        Assert(merged[0].Text == "Uno dos tres cuatro cinco. Seis siete ocho nueve diez\n…\nOnce doce trece catorce quince.",
            $"Solape recortado y hueco entre fragmentos marcado: «{merged[0].Text}»");
        var gap = RagContext.Join([a, c]);
        Assert(gap.Contains("\n…\n") && gap.StartsWith(a.Text) && gap.EndsWith(c.Text), "Los huecos se marcan con «…»");
        var sources = new List<SearchHit> { new(doc, 1, gap, 90) };
        Assert(AgentVerifier.Verify(new AgentAnswer("Respuesta [1]", sources), [doc], admin).IsValid,
            "Cada trozo del texto fusionado consta en la página");
        Assert(!AgentVerifier.Verify(new AgentAnswer("Respuesta [1]", [new(doc, 1, gap + "\n…\nFrase inventada.", 90)]), [doc], admin).IsValid,
            "Un trozo que no está en la página sigue bloqueándose");
        Assert(RagContext.MergeByPage([new(a, 0.9)], maxChars: 10)[0].Text.Length <= 11, "Respeta el tope de caracteres");
    }));
    await Check("Indexación: el vector se calcula con el título y la página delante del fragmento", () => Sync(() =>
    {
        var document = new RagDocument("doc-1", "sql", "Contrato Marco Cloud", "", [], new Dictionary<string, string>());
        var chunk = new RagChunk("doc-1:0", "doc-1", "CNT", "sql", 4, 0, 0, 20, "la tasa es del 99,99 %", "h");
        Assert(RagIngestionService.EmbeddingText(document, chunk) == "Contrato Marco Cloud · página 4\nla tasa es del 99,99 %");
        Assert(RagIngestionService.EmbeddingText(document with { Title = " " }, chunk) == chunk.Text, "Sin título, solo el texto");
        Assert(new RagOptions().MaxTokens <= 350, "Fragmentos dentro de la ventana de multilingual-e5-large");
    }));
    await Check("Reranker: sin cross-encoder conserva el orden híbrido salvo que se pida el semántico", async () =>
    {
        var calls = 0;
        var embedder = new FakeRagEmbedder(_ => { calls++; return [1f, 0f]; });
        var candidates = new List<RagHit>
        {
            new(new RagChunk("c1", "d", "X", "sql", 1, 0, 0, 1, "uno", "h1"), 0.9),
            new(new RagChunk("c2", "d", "X", "sql", 1, 1, 0, 1, "dos", "h2"), 0.5)
        };
        var factory = new FailingHttpFactory();
        var plain = new CrossEncoderRagReranker(RagSettings(), factory, new SemanticRagReranker(embedder),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossEncoderRagReranker>.Instance);
        var kept = await plain.RerankAsync("consulta", candidates);
        Assert(kept.Select(hit => hit.Chunk.Id).SequenceEqual(["c1", "c2"]) && calls == 0, "Sin URL ni opción, no se llama a embeddings");
        var semantic = new CrossEncoderRagReranker(RagSettings(o => o.SemanticRerankFallback = true), factory,
            new SemanticRagReranker(embedder), Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossEncoderRagReranker>.Instance);
        await semantic.RerankAsync("consulta", candidates);
        Assert(calls > 0, "Con SemanticRerankFallback=true se usa el reranker semántico");
        var failing = new CrossEncoderRagReranker(RagSettings(o => o.RerankerUrl = "http://127.0.0.1:9/"), factory,
            new SemanticRagReranker(embedder), Microsoft.Extensions.Logging.Abstractions.NullLogger<CrossEncoderRagReranker>.Instance);
        calls = 0;
        var afterFailure = await failing.RerankAsync("consulta", candidates);
        Assert(afterFailure.Select(hit => hit.Chunk.Id).SequenceEqual(["c1", "c2"]) && calls == 0, "Si el servicio falla, conserva el orden");
    });
    await Check("Chat: el marcado de un documento se conserva como texto", () => Sync(() =>
    {
        var blocks = AnswerFormatter.Format("<script>alert(1)</script> texto");
        Assert(blocks.Single() is AnswerParagraph { Content: [{ Kind: AnswerInlineKind.Text } part] } && part.Text.StartsWith("<script>"));
        Assert(AnswerFormatter.Format("   ").Count == 0);
    }));
    await Check("RAG Observador: periodo vacío", () => Sync(() =>
    {
        var empty = RagObservatory.Analyze([], readableCodes, observedNow.AddDays(-7), observedNow, true);
        Assert(empty.Queries == 0 && empty.Rate(RagOutcome.NoCoverage) == 0 && empty.Gaps.Count == 0 && empty.NeverCitedTotal == 3);
    }));
}

if (args.Contains("--governance"))
{
    var auditActor = new AuditActor("Ana Admin", "admin_general", "10.0.0.7");
    var auditNow = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    AuditChange TitleChange() => new(nameof(DocumentItem), "doc-1", "UPDATE",
        new Dictionary<string, string?> { ["Title"] = "Antes", ["InternalCode"] = "CNT-2026-AAA" },
        new Dictionary<string, string?> { ["Title"] = "Después", ["InternalCode"] = "CNT-2026-AAA" });

    await Check("Gobernanza bitácora: completa el evento instrumentado con entidad, antes/después e IP", () => Sync(() =>
    {
        var explicitEvent = new AuditEvent { Action = "UPDATE", UserName = "Ana Admin", Details = "UPDATE: x" };
        var added = AuditTrail.Apply([TitleChange()], [explicitEvent], auditActor, auditNow);
        Assert(added.Count == 0, "No duplica eventos si la operación ya está auditada");
        Assert(explicitEvent.Entity == nameof(DocumentItem) && explicitEvent.EntityId == "doc-1" && explicitEvent.Ip == "10.0.0.7");
        Assert(explicitEvent.Before!.Contains("Antes") && explicitEvent.After!.Contains("Después"));
    }));
    await Check("Gobernanza bitácora: registra mutaciones no instrumentadas", () => Sync(() =>
    {
        var added = AuditTrail.Apply([TitleChange()], [], auditActor, auditNow);
        var item = added.Single();
        Assert(item.Action == "DATA.UPDATE" && item.UserName == "Ana Admin" && item.DocumentCode == "CNT-2026-AAA" &&
            item.TimestampUTC == auditNow && item.Before!.Contains("Antes"), item.Details);
        Assert(AuditTrail.Apply([], [], auditActor, auditNow).Count == 0);
    }));
    await Check("Gobernanza bitácora: el antes/después se acota a 4 KB", () => Sync(() =>
    {
        var big = new Dictionary<string, string?> { ["ExecutiveSummary"] = new string('x', 9000) };
        Assert(AuditTrail.Snapshot(big)!.Length == AuditTrail.MaxSnapshotChars);
        Assert(AuditTrail.Snapshot(new Dictionary<string, string?>()) is null);
    }));
    await Check("Gobernanza bitácora: detecta cambios reales con el modelo EF y omite texto y binarios", () => Sync(() =>
    {
        using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=sin_conexion;Trusted_Connection=True").Options);
        var document = new DocumentItem { Id = "doc-9", InternalCode = "CNT-2026-ZZZ", Title = "Original", Author = "A", Department = "Legal",
            Category = "Contrato", Confidentiality = "Confidencial", Pages = [new DocumentPage(1, "texto secreto", "", [])] };
        context.Attach(document);
        document.Title = "Nuevo";
        document.Confidentiality = "Público";
        document.Pages = [new DocumentPage(1, "otro texto secreto", "", [])];
        context.ChangeTracker.DetectChanges();
        var change = GovernanceAuditInterceptor.Describe(context.Entry(document))!;
        Assert(change.Operation == "UPDATE" && change.EntityId == "doc-9");
        Assert(change.Before!["Title"] == "Original" && change.After!["Title"] == "Nuevo" && change.After["Confidentiality"] == "Público");
        Assert(!change.After!.ContainsKey(nameof(DocumentItem.PagesJson)) && !string.Join(" ", change.After.Values).Contains("secreto"),
            "El texto de las páginas nunca va a la bitácora");
        var user = new AppUser { Id = "usr-9", Name = "Nuevo", Email = "n@example.com", Role = "consultor", Department = "Legal" };
        context.Add(user);
        context.ChangeTracker.DetectChanges();
        var insert = GovernanceAuditInterceptor.Describe(context.Entry(user))!;
        Assert(insert.Operation == "INSERT" && insert.Before is null && insert.After!["Role"] == "consultor");
    }));
    await Check("Gobernanza bitácora: filtros por acción, entidad, usuario, estado, fechas y texto", () => Sync(() =>
    {
        var events = new List<AuditEvent>
        {
            new() { Action = "UPDATE", Entity = "DocumentItem", EntityId = "doc-1", UserName = "Ana", Status = "EXITOSO", TimestampUTC = auditNow, Details = "cambio de título" },
            new() { Action = "approval.requested", Entity = "ApprovalRequest", EntityId = "apr-1", UserName = "Ana", Status = "EXITOSO", TimestampUTC = auditNow.AddDays(-2), Details = "borrado" },
            new() { Action = "ACCESS_DENIED", UserName = "Luis", Status = "DENEGADO", TimestampUTC = auditNow.AddDays(-10), Details = "Operación denegada" }
        }.AsQueryable();
        Assert(AuditQuery.Apply(events, new AuditFilter(Entity: "DocumentItem")).Single().EntityId == "doc-1");
        Assert(AuditQuery.Apply(events, new AuditFilter(Actor: "Ana")).Count() == 2);
        Assert(AuditQuery.Apply(events, new AuditFilter(Status: "DENEGADO")).Single().UserName == "Luis");
        Assert(AuditQuery.Apply(events, new AuditFilter(From: auditNow.AddDays(-3))).Count() == 2);
        Assert(AuditQuery.Apply(events, new AuditFilter(Text: "apr-1")).Single().Action == "approval.requested");
        Assert(AuditQuery.Apply(events, new AuditFilter(Action: "UPDATE", To: auditNow.AddDays(-1))).Count() == 0);
    }));

    var governanceOn = new GovernanceOptions { ApprovalsEnabled = true };
    var requester = new AppUser { Id = "usr-admin", Role = "admin_general" };
    var reviewer = new AppUser { Id = "usr-audit", Role = "auditor" };
    ApprovalRequest Pending() => new() { RequestedById = requester.Id, Status = ApprovalStatus.Pending, ExpiresUtc = auditNow.AddHours(72) };

    await Check("Gobernanza aprobaciones: apagado por defecto, también en appsettings", () => Sync(() =>
    {
        var defaults = new GovernanceOptions();
        Assert(!defaults.ApprovalsEnabled && !defaults.SchedulerEnabled && !ApprovalRules.Requires(defaults, SensitiveAction.DeleteDocument));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "appsettings.json"))) directory = directory.Parent;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "appsettings.json")));
        var governance = json.RootElement.GetProperty("Governance");
        Assert(!governance.GetProperty("ApprovalsEnabled").GetBoolean() && !governance.GetProperty("SchedulerEnabled").GetBoolean());
    }));
    await Check("Gobernanza aprobaciones: solo las acciones configuradas requieren doble control", () => Sync(() =>
    {
        Assert(SensitiveAction.All.All(action => ApprovalRules.Requires(governanceOn, action)));
        Assert(!ApprovalRules.Requires(governanceOn, "UPLOAD"));
        Assert(!ApprovalRules.Requires(new GovernanceOptions { ApprovalsEnabled = true, SensitiveActions = [SensitiveAction.Reset] }, SensitiveAction.DeleteDocument));
    }));
    await Check("Gobernanza aprobaciones: decide otra persona con rol autorizado y solo si sigue pendiente", () => Sync(() =>
    {
        Assert(ApprovalRules.DecisionError(reviewer, Pending(), auditNow) is null);
        Assert(ApprovalRules.DecisionError(requester, Pending(), auditNow)!.Contains("propia"));
        Assert(ApprovalRules.DecisionError(new AppUser { Id = "usr-x", Role = "gestor_documental" }, Pending(), auditNow) is not null);
        var decided = Pending(); decided.Status = ApprovalStatus.Approved;
        Assert(ApprovalRules.DecisionError(reviewer, decided, auditNow)!.Contains("aprobada"), "Doble decisión = conflicto (409)");
        Assert(ApprovalRules.DecisionError(reviewer, Pending(), auditNow.AddHours(73))!.Contains("venció"));
        Assert(ApprovalRules.DecisionError(new AppUser { Id = "usr-admin-2", Role = "admin_general" }, Pending(), auditNow) is null);
    }));
    await Check("Gobernanza aprobaciones: solo bajar la confidencialidad es sensible", () => Sync(() =>
    {
        Assert(ApprovalRules.IsDowngrade("Confidencial", "Uso Interno") && ApprovalRules.IsDowngrade("Estrictamente Secreto", "Público"));
        Assert(!ApprovalRules.IsDowngrade("Uso Interno", "Confidencial") && !ApprovalRules.IsDowngrade("Público", "Público"));
        Assert(!ApprovalRules.IsDowngrade("Desconocido", "Público"));
    }));
    await Check("Gobernanza aprobaciones: la respuesta pendiente explica que no se aplicó nada", () => Sync(() =>
    {
        var request = new ApprovalRequest { Action = SensitiveAction.DeleteDocument, EntityLabel = "CNT-2026-AAA Contrato" };
        Assert(new ApprovalPendingException(request, false).Message.Contains("No se aplicará"));
        Assert(new ApprovalPendingException(request, true).Message.Contains("Ya hay una solicitud pendiente"));
        Assert(ApprovalContext.Current is null);
    }));

    await Check("Gobernanza rutinas: tres rutinas, vencimiento, pausa y siguiente ejecución", () => Sync(() =>
    {
        Assert(RoutineSchedule.Specs.Select(spec => spec.Key).SequenceEqual([RoutineSchedule.Scan, RoutineSchedule.DailySummary, RoutineSchedule.Cleanup]));
        var routine = new RoutineDefinition { Key = RoutineSchedule.Scan, IntervalMinutes = 60, Enabled = true, NextRunUtc = auditNow };
        Assert(RoutineSchedule.IsDue(routine, auditNow) && !RoutineSchedule.IsDue(routine, auditNow.AddMinutes(-1)));
        Assert(RoutineSchedule.IsDue(new RoutineDefinition { Enabled = true }, auditNow), "Sin próxima fecha se ejecuta en el siguiente ciclo");
        routine.Enabled = false;
        Assert(!RoutineSchedule.IsDue(routine, auditNow.AddDays(1)), "Una rutina en pausa no se ejecuta");
        Assert(RoutineSchedule.Next(routine, auditNow) == auditNow.AddMinutes(60));
        Assert(RoutineSchedule.Bound(new string('x', 5000)).Length == RoutineSchedule.MaxOutputChars);
        Assert(RoutineSchedule.Specs.Single(spec => spec.Key == RoutineSchedule.Cleanup).Description.Contains("bitácora no se borra"));
    }));
}

if (args.Contains("--ai"))
{
    await Check("Consumo IA: periodo = mes calendario UTC, también en los bordes", () => Sync(() =>
    {
        Assert(AiBudgetMath.CurrentPeriod(new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.FromHours(-5))) == "2026-12");
        Assert(AiBudgetMath.CurrentPeriod(new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.FromHours(2))) == "2026-11");
        Assert(AiBudgetMath.CurrentPeriod(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)) == "2027-01");
        var (start, end) = AiBudgetMath.PeriodRange("2026-02");
        Assert(start == new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero) && end == new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        Throws<FormatException>(() => AiBudgetMath.PeriodRange("2026-13"));
    }));
    await Check("Consumo IA: coste en micro-dólares enteros, truncado y nunca negativo", () => Sync(() =>
    {
        var pricing = new AiPricing(0.0005m, 0.0015m);
        Assert(AiBudgetMath.ComputeCostMicroUsd(2000, 1000, pricing) == 2500);
        Assert(AiBudgetMath.ComputeCostMicroUsd(1, 0, pricing) == 0, "Medio micro-dólar se trunca a 0");
        Assert(AiBudgetMath.ComputeCostMicroUsd(-50, -10, pricing) == 0);
        Assert(AiBudgetMath.ComputeCostMicroUsd(100_000, 100_000, AiPricing.Free) == 0);
        Assert(AiBudgetMath.ToMicroUsd(12.3456789m) == 12_345_678 && AiBudgetMath.ToMicroUsd(-1) == 0);
    }));
    await Check("Consumo IA: porcentaje, previsión y formato en USD (máx. 4 decimales)", () => Sync(() =>
    {
        Assert(AiBudgetMath.ComputePercentUsed(250_000, 1_000_000) == 25 && AiBudgetMath.ComputePercentUsed(5, 0) is null);
        Assert(AiBudgetMath.ComputeForecastMicroUsd(10, new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), "2026-11") == 30);
        Assert(AiBudgetMath.ComputeForecastMicroUsd(10, new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero), "2026-11") == 10);
        Assert(AiBudgetMath.ComputeForecastMicroUsd(10, new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), "2026-11") == 0);
        Assert(AiBudgetMath.FormatMicroUsd(1_500) == "$0.0015" && AiBudgetMath.FormatMicroUsd(0) == "$0.00" &&
            AiBudgetMath.FormatMicroUsd(2_000_000) == "$2.00" && AiBudgetMath.FormatMicroUsd(1_234_567) == "$1.2346");
    }));
    await Check("Consumo IA: corte por presupuesto, aviso sin cortar, cuota de tokens y presupuesto 0 = sin límite", () => Sync(() =>
    {
        var policy = new AiBudgetPolicy { MonthlyBudgetMicroUsd = 1_000_000, AlertPercent = 80, HardStop = true };
        Assert(AiBudgetMath.Evaluate(policy, 990_000, 0, 20_000) is { Allowed: false, IncidentKind: AiBudgetIncident.HardStop, LimitMicroUsd: 1_000_000 });
        Assert(AiBudgetMath.Evaluate(policy, 800_000, 0, 0) is { Allowed: true, IncidentKind: AiBudgetIncident.Warning });
        Assert(AiBudgetMath.Evaluate(policy, 100_000, 0, 0) is { Allowed: true, IncidentKind: null });
        Assert(AiBudgetMath.Evaluate(new AiBudgetPolicy { MonthlyBudgetMicroUsd = 1_000_000, HardStop = false }, 2_000_000, 0, 0)
            is { Allowed: true, IncidentKind: AiBudgetIncident.Warning }, "Sin corte, pasarse solo avisa");
        var quota = new AiBudgetPolicy { MonthlyBudgetMicroUsd = 0, MonthlyTokenQuota = 10_000, HardStop = true };
        Assert(AiBudgetMath.Evaluate(quota, 99_000_000, 10_000, 0) is { Allowed: false, IncidentKind: AiBudgetIncident.QuotaExceeded });
        Assert(AiBudgetMath.Evaluate(quota, 99_000_000, 9_999, 0) is { Allowed: true, IncidentKind: null }, "Con presupuesto 0 el gasto no limita");
        Assert(AiBudgetMath.Evaluate(new AiBudgetPolicy(), 50_000_000, 50_000_000, 0) is { Allowed: true, IncidentKind: null });
        Assert(AiBudgetMath.Evaluate(null, 1, 1, 1).Allowed);
    }));

    var noDatabase = new FailingFactory();
    AiGovernanceService Governance(AiGovernanceOptions? settings = null) => new(noDatabase, Session(), Options.Create(settings ?? new AiGovernanceOptions()),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AiGovernanceService>.Instance);
    await Check("Consumo IA: precio local gratis, catálogo de OpenCode y configuración", () => Sync(() =>
    {
        var paid = new AssistantProfile { Group = "Pago", InputCostPerMillion = 0.27m, OutputCostPerMillion = 1.10m };
        Assert(Governance().ResolvePricing("opencode", "lmstudio", "qwen", null).IsFree);
        Assert(Governance().ResolvePricing("opencode", "lmstudio", "m", new AssistantProfile { Group = "Locales", InputCostPerMillion = 9 }).IsFree);
        Assert(Governance().ResolvePricing("opencode", "deepseek", "flash", paid) == new AiPricing(0.00027m, 0.0011m));
        var configured = new AiGovernanceOptions { UsdPer1kInput = 0.001m, UsdPer1kOutput = 0.002m };
        configured.Pricing["deepseek/flash"] = new AiPricing(0.5m, 0.6m);
        Assert(Governance(configured).ResolvePricing("opencode", "deepseek", "flash", paid) == new AiPricing(0.5m, 0.6m), "La configuración prevalece");
        Assert(Governance(configured).ResolvePricing("opencode", "x", "y", new AssistantProfile { Group = "Pago" }) == new AiPricing(0.001m, 0.002m));
        Assert(Governance(configured).ResolvePricing("opencode", "x", "y", new AssistantProfile { Group = "Gratis" }).IsFree);
    }));
    await Check("Consumo IA: sin base de datos degrada sin bloquear y el registro nunca tumba el proceso", async () =>
    {
        await Governance().AssertCanSpendAsync("assistant_rag", 1_000_000);
        await Governance().RecordAsync(new AiUsageRecord("assistant_rag", "p", "m", "k", "v", "h", 1, 1, false, 1, 1, "ok"));
    });

    AiCallContext GuardContext() => new("assistant_rag", "opencode", "deepseek", "flash", "assistant.rag.default", "SYSTEM PROMPT",
        new string('x', 4000), new AiPricing(0.001m, 0.002m), 600, nameof(AgentTrace), "trace-1");
    await Check("Consumo IA: comprueba antes y registra una vez después de cada llamada real", async () =>
    {
        var fake = new FakeAiGovernance();
        var answer = await AiCallGuard.RunAsync(fake, GuardContext(), () =>
        {
            fake.Events.Add("call");
            return Task.FromResult(new AgentAnswer("respuesta", [], "OpenCode", 1200, 300));
        });
        Assert(answer.Text == "respuesta" && fake.Events.SequenceEqual(["assert", "call", "record"]), string.Join(",", fake.Events));
        var row = fake.Records.Single();
        Assert(row is { TokensIn: 1200, TokensOut: 300, TokensEstimated: false, CostMicroUsd: 1800, Status: "ok", EntityId: "trace-1" } &&
            row.PromptVersion.Length == 8 && row.RequestHash.Length == 16, row.ToString());
        Assert(fake.LastEstimate > 0, "Estima el coste antes de llamar");
    });
    await Check("Consumo IA: estima tokens si el proveedor no los informa y registra los fallos", async () =>
    {
        var fake = new FakeAiGovernance();
        await AiCallGuard.RunAsync(fake, GuardContext(), () => Task.FromResult(new AgentAnswer(new string('r', 400), [])));
        Assert(fake.Records.Single() is { TokensEstimated: true, TokensOut: 100 } estimated && estimated.TokensIn > 1000);
        await ThrowsAsync<HttpRequestException>(() => AiCallGuard.RunAsync(fake, GuardContext(), () => throw new HttpRequestException("caído")));
        Assert(fake.Records[1] is { Status: "error", ErrorCode: "connection", CostMicroUsd: 0 });
    });
    await Check("Consumo IA: con el presupuesto agotado no llama al proveedor ni registra", async () =>
    {
        var fake = new FakeAiGovernance { Block = true };
        var called = false;
        await ThrowsAsync<AiQuotaExceededException>(() => AiCallGuard.RunAsync(fake, GuardContext(), () =>
        {
            called = true;
            return Task.FromResult(new AgentAnswer("x", []));
        }));
        Assert(!called && fake.Records.Count == 0);
        Assert(AgentObservability.ErrorCode(new AiQuotaExceededException("Presupuesto mensual de IA agotado.", 1, 1, AiBudgetIncident.HardStop)) == "ai_quota_exceeded");
        Assert(AnswerFormatter.Format("Fragmento\n[CNT-2026-AAA, p. 1]\n\nPresupuesto mensual de IA agotado ($1.00 de $1.00). Se muestra la respuesta extractiva.")[^1] is AnswerNote);
    });
    await Check("Consumo IA: lectura para auditoría y administración; presupuesto solo administración", () => Sync(() =>
    {
        Assert(Permissions.CanReadAiUsage(admin) && Permissions.CanManageAiBudget(admin));
        Assert(Permissions.CanReadAiUsage(auditor) && !Permissions.CanManageAiBudget(auditor));
        Assert(!Permissions.CanReadAiUsage(manager) && !Permissions.CanManageAiBudget(manager));
    }));
    await Check("Consumo IA: el informe agrupa por función y modelo y cuenta errores", () => Sync(() =>
    {
        AiUsageLedger Row(string feature, string model, long cost, string status = "ok") => new()
        {
            Period = "2026-11", Feature = feature, Provider = "deepseek", Model = model, TokensIn = 100, TokensOut = 50,
            CostMicroUsd = cost, Status = status, CreatedUtc = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero)
        };
        var report = AiBudgetMath.BuildReport("2026-11",
            [Row("assistant_rag", "flash", 300), Row("assistant_rag", "flash", 200), Row("catalog_suggestion", "pro", 900), Row("assistant_rag", "flash", 0, "error")],
            new AiBudgetPolicy { MonthlyBudgetMicroUsd = 10_000 }, [], new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero));
        Assert(report is { Calls: 4, Errors: 1, SpentMicroUsd: 1400, TokensIn: 400, PercentUsed: 14, ForecastMicroUsd: 1400 }, report.ToString());
        Assert(report.Features[0] == new AiFeatureUsage("catalog_suggestion", 1, 100, 50, 900) && report.Features[1].Calls == 3);
        Assert(report.Models.Count == 2 && AiBudgetMath.BuildReport("2026-11", [], null, [], DateTimeOffset.UtcNow).PercentUsed is null);
    }));
}

if (args.Contains("--mcp"))
{
    await Check("MCP: el hash de token es estable y no reversible", () => Sync(() =>
    {
        var first = McpTokenService.Hash("dsmcp_token_de_prueba");
        var second = McpTokenService.Hash("dsmcp_token_de_prueba");
        var other = McpTokenService.Hash("dsmcp_otro");
        Assert(first == second && first.Length == 64 && first != other);
        Assert(!first.Contains("token_de_prueba", StringComparison.Ordinal));
    }));
}

if (args.Contains("--langgraph"))
{
    var graphOptions = Options.Create(new AssistantOptions
    {
        UseLangGraph = true, LangGraphMaxSources = 6, LangGraphMaxRetries = 1, LangGraphTimeoutSeconds = 30
    });
    var graphOpenCode = Options.Create(new OpenCodeOptions
    {
        Enabled = true, BaseUrl = "http://127.0.0.1:4097", Password = "secreto",
        PythonExecutable = "python", Agent = "zodiac-asistente"
    });
    var graphRuntime = new AssistantRuntime(Options.Create(new AssistantOptions()),
        OpenCodeFor(OpenCodeCatalogMock(catalogProvidersJson, catalogModelsJson)),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
    await graphRuntime.RefreshAsync(force: true);
    var graphDocument = seed.Documents.First(document => document.Pages.Count > 0);
    var graphPage = graphDocument.Pages.First();
    var graphHit = new SearchHit(graphDocument, graphPage.PageNumber, graphPage.ExtractedText, 100);
    var graphSnapshot = new AppSnapshot(admin, seed.Documents, seed.Users, seed.Departments, []);
    LangGraphRagService Graph(FakeLangGraphRunner runner, bool enabled = true) =>
        new(enabled ? graphOptions : Options.Create(new AssistantOptions { UseLangGraph = false }),
            graphOpenCode, graphRuntime, runner,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LangGraphRagService>.Instance);

    await Check("LangGraph C#: respuesta válida con runner falso", async () =>
    {
        var runner = new FakeLangGraphRunner
        {
            Response = "{\"status\":\"ok\",\"answer\":\"Resumen verificado [1].\",\"citations\":[1]}"
        };
        var answer = await Graph(runner).TryGenerateAsync("Resume el documento", [graphHit], graphSnapshot,
            AnswerStyle.Summary, default);
        Assert(answer is not null && answer.Text.Contains("Resumen verificado") &&
               answer.Sources.Single().Document.InternalCode == graphDocument.InternalCode);
        Assert(answer!.Provider.Contains("LangGraph", StringComparison.Ordinal));
        Assert(runner.Calls == 1 && runner.LastRequest!.Contains("\"max_sources\"", StringComparison.Ordinal));
    });
    await Check("LangGraph C#: cita fuera de rango se rechaza sin lanzar", async () =>
    {
        var runner = new FakeLangGraphRunner
        {
            Response = "{\"status\":\"ok\",\"answer\":\"Inventado [9].\",\"citations\":[9]}"
        };
        Assert(await Graph(runner).TryGenerateAsync("Resume", [graphHit], graphSnapshot,
            AnswerStyle.Summary, default) is null);
    });
    await Check("LangGraph C#: error del runner cae al camino anterior", async () =>
    {
        var runner = new FakeLangGraphRunner { Failure = new InvalidOperationException("timeout") };
        Assert(await Graph(runner).TryGenerateAsync("Resume", [graphHit], graphSnapshot,
            AnswerStyle.Summary, default) is null);
    });
    await Check("LangGraph C#: bandera apagada no llama al runner", async () =>
    {
        var runner = new FakeLangGraphRunner { Response = "{}" };
        Assert(await Graph(runner, enabled: false).TryGenerateAsync("Resume", [graphHit], graphSnapshot,
            AnswerStyle.Summary, default) is null && runner.Calls == 0);
    });
    await Check("LangGraph C#: fuente no autorizada no se envía", async () =>
    {
        var runner = new FakeLangGraphRunner { Response = "{}" };
        var empty = new AppSnapshot(admin, [], seed.Users, seed.Departments, []);
        Assert(await Graph(runner).TryGenerateAsync("Resume", [graphHit], empty,
            AnswerStyle.Summary, default) is null && runner.Calls == 0);
    });
}

if (args.Contains("--database"))
{
    var connection = Environment.GetEnvironmentVariable("DOCUSEARCH_TEST_CONNECTION")
        ?? throw new InvalidOperationException("Configura DOCUSEARCH_TEST_CONNECTION con una base exclusiva cuyo nombre termine en _tests.");
    var settings = new SqlConnectionStringBuilder(connection);
    if (!settings.InitialCatalog.EndsWith("_tests", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Las pruebas de integración requieren una base con sufijo _tests.");
    var factory = new TestFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection, sql => sql.UseCompatibilityLevel(150)).Options);
    var session = Session();
    var service = new DocumentService(factory, session, processor);
    using var offlineHttp = new HttpClient();
    var telemetry = new AgentObservability(factory, session, Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentObservability>.Instance);
    var agent = new LocalAgent(service, session, telemetry);
    await Check("SQL Server: esquema e inicialización idempotente", async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        await DatabaseSetup.InitializeAsync(db);
        await DatabaseSetup.InitializeAsync(db);
        await service.ResetAsync("RESTABLECER");
        Assert((await service.LoadAsync()).Documents.Count >= 5);
        // La base solo siembra los tres perfiles TOTP; el resto de roles se prueban con
        // usuarios temporales creados por el administrador dentro de esta base de tests.
        var dbConsultant = new AppUser
        {
            Name = "Consultor Checks", Email = "consultor.checks@example.test",
            Role = "consultor", Department = admin.Department, Status = "Activo"
        };
        await service.SaveUserAsync(dbConsultant);
        consultant = dbConsultant;
    });
    await Check("SQL Server: carga real, original persistente y metadatos", async () =>
    {
        await service.UploadAsync(new() { Title = "Integration fixture", Author = admin.Name, Department = admin.Department }, pdf);
        var row = (await service.LoadAsync()).Documents.Single(d => d.Title == "Integration fixture");
        Assert((await service.FileAsync(row.Id, true)).SequenceEqual(pdf));
        Assert((await service.LoadAsync()).Documents.Single(d => d.Id == row.Id).DownloadsCount == 1);
        await service.AddNoteAsync(row.Id, "Persistent test note", 1);
        row.Title = "Edited fixture";
        await service.EditAsync(row);
        var loaded = (await service.LoadAsync()).Documents.Single(d => d.Id == row.Id);
        Assert(loaded.Title == "Edited fixture" && loaded.Notes.Single().Text == "Persistent test note");
        await service.ReprocessAsync(row.Id);
        await service.DeleteAsync(row.Id);
        Assert(!(await service.LoadAsync()).Documents.Any(d => d.Id == row.Id));
    });
    await Check("SQL Server: asistente local cita la página correcta", async () =>
    {
        var answer = await agent.AskAsync("Extrae montos de CNT-2025-0892");
        Assert(answer.Text.Contains("$450,000 USD") && answer.Sources.Any(h => h.PageNumber == 4));
        var followup = await agent.AskAsync("Resume ese expediente");
        Assert(followup.Text.Contains("Cloud"));
    });
    await Check("SQL Server: el asistente responde en extractivo si el motor falla", async () =>
    {
        using var http = new HttpClient(new TestHttpHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/prompt", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return Task.FromResult(JsonResponse(path switch
            {
                "/api/info" => """{"version":"2.2.5"}""",
                "/api/provider" => catalogProvidersJson,
                "/api/model" => catalogModelsJson,
                "/api/agent" => """{"data":[{"id":"zodiac-asistente"}]}""",
                "/api/session" => """{"data":{"id":"ses_fallback"}}""",
                _ => "{}"
            }));
        }));
        var failing = OpenCodeFor(http);
        var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), failing,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
        await runtime.RefreshAsync(force: true);
        var fallbackAgent = new LocalAgent(service, session, telemetry, failing, runtime,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalAgent>.Instance);
        var answer = await fallbackAgent.AskAsync("Resume POL-2025-0012");
        Assert(answer.Provider.Contains("extractiva", StringComparison.Ordinal) && answer.Sources.Count > 0);
    });
    await Check("SQL Server: permisos no dependen de la interfaz", async () =>
    {
        await service.SwitchProfileAsync(consultant.Id);
        var snapshot = await service.LoadAsync();
        Assert(snapshot.Documents.Count == 2 && snapshot.Audit.Count == 0);
        await ThrowsAsync<UnauthorizedAccessException>(() => service.ViewAsync(contract.Id));
        await ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(policy.Id));
        await ThrowsAsync<UnauthorizedAccessException>(() => service.ExportAuditAsync());
        var answer = await agent.AskAsync("Resume FIN-2024-Q4-01");
        Assert(answer.Sources.Count == 0 && !answer.Text.Contains("EBITDA"));
        await service.SwitchProfileAsync(admin.Id);
        Assert((await service.ExportAuditAsync()).Any(a => a.Action == "ACCESS_DENIED"));
    });
    await Check("SQL Server: instantánea parcial y subconjunto legible respetan permisos", async () =>
    {
        var full = await service.LoadAsync();
        var light = await service.LoadAsync(SnapshotParts.None);
        Assert(light.Documents.Select(d => d.Id).SequenceEqual(full.Documents.Select(d => d.Id)), "Mismos documentos legibles.");
        Assert(light.Users.Count == 0 && light.Departments.Count == 0 && light.Audit.Count == 0, "Sin directorio ni bitácora.");
        Assert(light.Documents.All(d => d.PageVectors.Count == 0), "Sin vectores por página.");
        Assert(full.Users.Count > 0 && full.Departments.Count > 0, "LoadAsync() conserva la instantánea completa.");

        var allCodes = full.Documents.Select(d => d.InternalCode).ToList();
        var subset = await service.LoadReadableSubsetAsync(codes: allCodes.Select(code => code.ToLowerInvariant()));
        Assert(subset.Documents.Count == allCodes.Count, "Los códigos no distinguen mayúsculas.");
        Assert((await service.LoadReadableSubsetAsync()).Documents.Count == 0, "Sin ids ni códigos no devuelve nada.");

        await service.SwitchProfileAsync(consultant.Id);
        try
        {
            var readable = (await service.LoadAsync()).Documents.Select(d => d.Id).ToHashSet();
            var asConsultant = await service.LoadReadableSubsetAsync(ids: full.Documents.Select(d => d.Id));
            Assert(asConsultant.Actor.Id == consultant.Id, "El actor es el perfil actual.");
            Assert(asConsultant.Documents.Count == readable.Count && asConsultant.Documents.All(d => readable.Contains(d.Id)),
                "El subconjunto excluye lo que el consultor no puede leer.");
            Assert(readable.Count < full.Documents.Count, "El escenario debe tener documentos no legibles para el consultor.");
        }
        finally { await service.SwitchProfileAsync(admin.Id); }
    });
    await Check("SQL Server + RAG: la revalidación final bloquea fuentes revocadas durante la consulta", async () =>
    {
        var full = await service.LoadAsync();
        await service.SwitchProfileAsync(consultant.Id);
        var consultantIds = (await service.LoadAsync()).Documents.Select(d => d.Id).ToHashSet();
        await service.SwitchProfileAsync(admin.Id);
        var secret = full.Documents.First(d => !consultantIds.Contains(d.Id) && d.Pages.Count > 0);
        var page = secret.Pages[0];
        var term = TextAnalysis.Terms($"{secret.Title} {page.ExtractedText}").First(t => t.Length >= 4);
        var hit = new RagHit(new RagChunk("chk-1", secret.Id, secret.InternalCode, "sql", page.PageNumber, 0, 0,
            page.ExtractedText.Length, $"{term} {page.ExtractedText}", "hash"), 0.9);
        var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(new HttpClient(), enabled: false),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
        RagQueryService Rag(Func<Task>? during = null) => new(service, new FakeRagRetriever([hit], during),
            new FakeRagEmbedder(_ => [1f, 0f]), runtime, null,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RagQueryService>.Instance);
        try
        {
            var allowed = await Rag().QueryAsync(term, generate: false);
            Assert(allowed.Outcome != RagOutcome.AccessDenied && allowed.Sources.Any(s => s.DocumentCode == secret.InternalCode),
                "Sin cambios de permisos, la fuente se devuelve.");
            var revoked = await Rag(() => service.SwitchProfileAsync(consultant.Id)).QueryAsync(term, generate: false);
            Assert(revoked.Outcome == RagOutcome.AccessDenied && revoked.Sources.Count == 0,
                "Si el perfil pierde el acceso durante la consulta, no se muestran fuentes.");
        }
        finally { await service.SwitchProfileAsync(admin.Id); }
    });
    await Check("SQL Server + OpenCode: solo se envían fuentes autorizadas al consultor", async () =>
    {
        var allCodes = (await service.LoadAsync()).Documents.Select(d => d.InternalCode).ToList();
        await service.SwitchProfileAsync(consultant.Id);
        try
        {
            var readable = (await service.LoadAsync()).Documents.Select(d => d.InternalCode).ToHashSet();
            var hidden = allCodes.Where(code => !readable.Contains(code)).ToList();
            Assert(hidden.Count > 0, "El escenario necesita expedientes que el consultor no pueda leer.");
            string? prompt = null;
            // Protocolo OpenCode v2 (sesión + prompt + mensajes); el prompt lleva las fuentes enviadas al modelo.
            using var http = OpenCodeMock("""{"answer":"Resumen autorizado","citations":[1]}""", request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/prompt", StringComparison.Ordinal))
                    prompt = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            });
            var answer = await new LocalAgent(service, session, telemetry, OpenCodeFor(http)).AskAsync("Resume POL-2025-0012");
            Assert(prompt is not null, "Se llegó a llamar al modelo.");
            Assert(prompt!.Contains("POL-2025-0012", StringComparison.Ordinal), "El prompt lleva la fuente legible.");
            Assert(hidden.All(code => !prompt.Contains(code, StringComparison.OrdinalIgnoreCase)),
                "El prompt no menciona expedientes que el consultor no puede leer.");
            Assert(answer.Provider.Contains("OpenCode") && answer.Sources.All(s => readable.Contains(s.Document.InternalCode)));
        }
        finally { await service.SwitchProfileAsync(admin.Id); }
    });
    await Check("SQL Server + OpenCode: revocación durante generación bloquea respuesta", async () =>
    {
        try
        {
            var prompted = false;
            // El perfil pierde el acceso justo cuando el modelo recibe el prompt.
            using var http = OpenCodeMock("""{"answer":"Respuesta ahora revocada","citations":[1]}""", request =>
            {
                if (!request.RequestUri!.AbsolutePath.EndsWith("/prompt", StringComparison.Ordinal)) return;
                prompted = true;
                service.SwitchProfileAsync(consultant.Id).GetAwaiter().GetResult();
            });
            await ThrowsAsync<UnauthorizedAccessException>(() => new LocalAgent(service, session, telemetry, OpenCodeFor(http)).AskAsync("Resume FIN-2024-Q4-01"));
            Assert(prompted, "La revocación ocurrió durante la generación, no antes.");
        }
        finally { await service.SwitchProfileAsync(admin.Id); }
    });
    await Check("SQL Server: trazas persistentes, privadas y con acceso restringido", async () =>
    {
        var marker = $"Private-{Guid.NewGuid():N}";
        var before = (await telemetry.LoadAsync()).Count;
        await agent.AskAsync(marker);
        var traces = await telemetry.LoadAsync();
        Assert(traces.Count == before + 1);
        Assert(traces[0].Status == "no_sources" && traces[0].DurationMs >= 0 &&
            traces[0].Engine == "extractive" && traces[0].InputTokens is null);
        Assert(!JsonSerializer.Serialize(traces).Contains(marker));
        await ThrowsAsync<ValidationException>(() => agent.AskAsync(""));
        traces = await telemetry.LoadAsync();
        Assert(traces.Any(t => t.Status == "error" && t.ErrorCode == "invalid_input"));
        Assert(traces.Any(t => t.Status == "error" && t.ErrorCode == "access_denied"));
        await using (var db = await factory.CreateDbContextAsync())
            Assert(!await db.Audit.AnyAsync(a => a.Action == "AGENT_QUERY" && a.Details.Contains(marker)));
        await service.SwitchProfileAsync(consultant.Id);
        try { await ThrowsAsync<UnauthorizedAccessException>(() => telemetry.LoadAsync()); }
        finally { await service.SwitchProfileAsync(admin.Id); }
        await service.SwitchProfileAsync(auditor.Id);
        try { Assert((await telemetry.LoadAsync()).Count > 0); }
        finally { await service.SwitchProfileAsync(admin.Id); }
    });
    await Check("SQL Server: administración y cuotas reales", async () =>
    {
        var department = new DepartmentInfo { Name = "Checks Department", Lead = "Test Lead", AllocatedStorageMB = 1 };
        await service.SaveDepartmentAsync(department);
        var user = new AppUser { Name = "Checks User", Email = "checks@example.test", Department = department.Name };
        await service.SaveUserAsync(user);
        user.Status = "Inactivo";
        await service.SaveUserAsync(user);
        await ThrowsAsync<ValidationException>(() => service.SwitchProfileAsync(user.Id));
        await service.SaveUserAsync(user, true);
        var oversized = PdfFixture.CreateWithPadding(1024 * 1024, "quota");
        await ThrowsAsync<ValidationException>(() => service.UploadAsync(new() { Title = "Quota", Author = "Test", Department = department.Name }, oversized));
    });
    await Check("SQL Server: importación transaccional y notas preservadas", async () =>
    {
        var backup = new
        {
            schemaVersion = 1, documents = new[] { contract }, users = Array.Empty<AppUser>(),
            departments = Array.Empty<DepartmentInfo>(), files = new Dictionary<string, string>()
        };
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(backup, Catalog.Json));
        await new LegacyImport(factory, session, processor).ImportAsync(input);
        Assert((await service.LoadAsync()).Documents.Single(d => d.Id == contract.Id).Notes.Count == 1);
    });
    await Check("SQL Server: notas concurrentes no pierden actualizaciones", async () =>
    {
        await service.UploadAsync(new() { Title = "Concurrent notes fixture", Author = admin.Name, Department = admin.Department }, pdf);
        var row = (await service.LoadAsync()).Documents.Single(d => d.Title == "Concurrent notes fixture");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 6).Select(i => service.AddNoteAsync(row.Id, $"Concurrent note {i}", 1)));
            var saved = (await service.LoadAsync()).Documents.Single(d => d.Id == row.Id);
            Assert(saved.Notes.Count == 6 && saved.Notes.Select(n => n.Text).Distinct().Count() == 6);
            Assert((await service.FileAsync(row.Id, false)).SequenceEqual(pdf));
        }
        finally { await service.DeleteAsync(row.Id); }
    });
    await Check("SQL Server: fallo de importación revierte cambios ya guardados en la transacción", async () =>
    {
        var invalid = JsonSerializer.Deserialize<DocumentItem>(JsonSerializer.Serialize(contract, Catalog.Json), Catalog.Json)!;
        invalid.Category = "Invalid category";
        var department = new DepartmentInfo { Name = "Rollback fixture", Lead = "Test lead" };
        var backup = new
        {
            schemaVersion = 1, documents = new[] { invalid }, users = Array.Empty<AppUser>(),
            departments = new[] { department }, files = new Dictionary<string, string>()
        };
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(backup, Catalog.Json));
        await ThrowsAsync<InvalidDataException>(() => new LegacyImport(factory, session, processor).ImportAsync(input));
        var saved = await service.LoadAsync();
        Assert(saved.Departments.All(d => d.Id != department.Id));
        Assert(saved.Documents.Single(d => d.Id == contract.Id).Category == contract.Category);
    });

    // --- Gobernanza contra SQL Server real: doble control, trigger y rutinas ---
    var governance = Options.Create(new GovernanceOptions { ApprovalsEnabled = true });
    var adminServices = new MapServiceProvider();
    var adminApprovals = new ApprovalService(factory, session, governance, adminServices);
    var adminDocs = new DocumentService(factory, session, processor, approvals: adminApprovals);
    adminServices.Add(adminDocs);
    var auditorSession = Session(auditor.Id);
    var auditorServices = new MapServiceProvider();
    var auditorApprovals = new ApprovalService(factory, auditorSession, governance, auditorServices);
    var auditorDocs = new DocumentService(factory, auditorSession, processor, approvals: auditorApprovals);
    auditorServices.Add(auditorDocs);
    async Task<DocumentItem> GovernanceFixture(string title)
    {
        await service.UploadAsync(new() { Title = title, Author = admin.Name, Department = admin.Department }, pdf);
        return (await service.LoadAsync()).Documents.Single(d => d.Title == title);
    }
    await Check("SQL Server gobernanza: borrar pide aprobación, no aplica nada y lo aplica al aprobar", async () =>
    {
        var target = await GovernanceFixture("Governance delete fixture");
        await ThrowsAsync<ApprovalPendingException>(() => adminDocs.DeleteAsync(target.Id));
        Assert((await service.LoadAsync()).Documents.Any(d => d.Id == target.Id), "La primera llamada no aplica nada (202)");
        try { await adminDocs.DeleteAsync(target.Id); throw new InvalidOperationException("Se esperaba una solicitud pendiente"); }
        catch (ApprovalPendingException repeated) { Assert(repeated.Message.Contains("Ya hay una solicitud pendiente")); }
        var request = (await auditorApprovals.ListAsync()).Single(r => r.EntityId == target.Id && r.Status == ApprovalStatus.Pending);
        await ThrowsAsync<ValidationException>(() => adminApprovals.ApproveAsync(request.Id, null));
        await auditorApprovals.ApproveAsync(request.Id, "Revisado");
        Assert(!(await service.LoadAsync()).Documents.Any(d => d.Id == target.Id), "Se aplica solo al aprobar");
        await ThrowsAsync<ApprovalConflictException>(() => auditorApprovals.ApproveAsync(request.Id, null));
        var trail = (await service.ExportAuditAsync()).Where(e => e.EntityId == request.Id || e.Details.Contains(request.Id[..8])).Select(e => e.Action).ToList();
        Assert(trail.Contains("approval.requested") && trail.Contains("approval.approved") && trail.Contains("DELETE"), string.Join(",", trail));
    });
    await Check("SQL Server gobernanza: rechazar descarta la acción", async () =>
    {
        var target = await GovernanceFixture("Governance reject fixture");
        await ThrowsAsync<ApprovalPendingException>(() => adminDocs.DeleteAsync(target.Id));
        var request = (await auditorApprovals.ListAsync()).Single(r => r.EntityId == target.Id && r.Status == ApprovalStatus.Pending);
        await auditorApprovals.RejectAsync(request.Id, "No procede");
        Assert((await service.LoadAsync()).Documents.Any(d => d.Id == target.Id));
        Assert((await auditorApprovals.ListAsync()).Single(r => r.Id == request.Id).Status == ApprovalStatus.Rejected);
        await ThrowsAsync<ApprovalConflictException>(() => auditorApprovals.RejectAsync(request.Id, null));
        await service.DeleteAsync(target.Id);
    });
    await Check("SQL Server gobernanza: bajar la confidencialidad espera aprobación y subirla no", async () =>
    {
        var target = await GovernanceFixture("Governance level fixture");
        target.Confidentiality = "Confidencial";
        await adminDocs.EditAsync(target);
        target.Confidentiality = "Público";
        await ThrowsAsync<ApprovalPendingException>(() => adminDocs.EditAsync(target));
        Assert((await service.LoadAsync()).Documents.Single(d => d.Id == target.Id).Confidentiality == "Confidencial");
        var request = (await auditorApprovals.ListAsync()).Single(r => r.EntityId == target.Id && r.Status == ApprovalStatus.Pending);
        await auditorApprovals.ApproveAsync(request.Id, null);
        Assert((await service.LoadAsync()).Documents.Single(d => d.Id == target.Id).Confidentiality == "Público");
        await service.DeleteAsync(target.Id);
    });
    await Check("SQL Server gobernanza: la bitácora rechaza UPDATE y DELETE", async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        await ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE [Audit] SET [Details] = N'alterado'"));
        await ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM [Audit]"));
        Assert(await db.Audit.AnyAsync());
    });
    await Check("SQL Server gobernanza: el interceptor guarda el antes/después de una edición", async () =>
    {
        var audited = new TestFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection, sql => sql.UseCompatibilityLevel(150))
            .AddInterceptors(new GovernanceAuditInterceptor(session)).Options);
        var target = await GovernanceFixture("Governance diff fixture");
        target.Title = "Governance diff edited";
        await new DocumentService(audited, session, processor).EditAsync(target);
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.Audit.Where(e => e.Action == "UPDATE" && e.EntityId == target.Id).OrderByDescending(e => e.TimestampUTC).FirstAsync();
        Assert(row.Before!.Contains("Governance diff fixture") && row.After!.Contains("Governance diff edited"));
        await service.DeleteAsync(target.Id);
    });
    await Check("SQL Server consumo IA: corte por presupuesto con incidente idempotente y permisos", async () =>
    {
        await using (var clean = await factory.CreateDbContextAsync())
        {
            // Base exclusiva de tests: se parte de un mes sin consumo ni incidentes.
            await clean.AiUsageLedger.ExecuteDeleteAsync();
            await clean.AiBudgetIncidents.ExecuteDeleteAsync();
        }
        var aiOptions = Options.Create(new AiGovernanceOptions());
        var aiAdmin = new AiGovernanceService(factory, session, aiOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<AiGovernanceService>.Instance);
        var aiAuditor = new AiGovernanceService(factory, Session(auditor.Id), aiOptions, Microsoft.Extensions.Logging.Abstractions.NullLogger<AiGovernanceService>.Instance);
        await ThrowsAsync<UnauthorizedAccessException>(() => aiAuditor.UpdateBudgetAsync(1, 80, true, 0));
        await aiAdmin.UpdateBudgetAsync(AiBudgetMath.ToMicroUsd(0.01m), 50, true, 0);
        await aiAdmin.RecordAsync(new AiUsageRecord("assistant_rag", "deepseek", "flash", "assistant.rag.default", "v1", "h1", 1000, 500, false, 6_000, 900, "ok"));
        await aiAdmin.AssertCanSpendAsync("assistant_rag", 0);
        await aiAdmin.RecordAsync(new AiUsageRecord("assistant_rag", "deepseek", "flash", "assistant.rag.default", "v1", "h2", 1000, 500, false, 5_000, 900, "ok"));
        await ThrowsAsync<AiQuotaExceededException>(() => aiAdmin.AssertCanSpendAsync("assistant_rag", 0));
        await ThrowsAsync<AiQuotaExceededException>(() => aiAdmin.AssertCanSpendAsync("assistant_summary", 0));
        var usage = await aiAuditor.GetUsageAsync();
        Assert(usage.SpentMicroUsd >= 11_000 && usage.Calls >= 2);
        var hardStop = usage.Incidents.Single(incident => incident.Kind == AiBudgetIncident.HardStop);
        Assert(hardStop.Occurrences >= 2 && hardStop.LastFeature == "assistant_summary", "Un solo incidente por periodo y tipo");
        Assert(usage.Incidents.Any(incident => incident.Kind == AiBudgetIncident.Warning));
        await aiAdmin.UpdateBudgetAsync(0, 80, false, 0);
        await aiAdmin.AssertCanSpendAsync("assistant_rag", 0);
    });
    await Check("SQL Server gobernanza: rutinas con historial, pausa y bloqueo entre instancias", async () =>
    {
        var routineServices = new MapServiceProvider();
        routineServices.Add(adminApprovals);
        var routines = new RoutineService(factory, session, governance, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            routineServices, Microsoft.Extensions.Logging.Abstractions.NullLogger<RoutineService>.Instance);
        var run = await routines.RunNowAsync(RoutineSchedule.Cleanup);
        Assert(run is { Status: "OK" } && run.Output.Contains("bitácora se conserva"), run?.Output ?? "sin ejecución");
        Assert((await routines.ListAsync()).Single(r => r.Routine.Key == RoutineSchedule.Cleanup).Runs.Any(r => r.Id == run!.Id));
        await routines.SetEnabledAsync(RoutineSchedule.Cleanup, false);
        Assert(!(await routines.ListAsync()).Single(r => r.Routine.Key == RoutineSchedule.Cleanup).Routine.Enabled);
        await routines.SetEnabledAsync(RoutineSchedule.Cleanup, true);
        await using var holder = new SqlConnection(connection);
        await holder.OpenAsync();
        await using (var take = new SqlCommand("EXEC sp_getapplock @Resource = 'docusearch:routine:RESUMEN_DIARIO', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0", holder))
            await take.ExecuteNonQueryAsync();
        Assert(await routines.RunNowAsync(RoutineSchedule.DailySummary) is null, "Con el bloqueo tomado por otra instancia no se repite");
        await using (var release = new SqlCommand("EXEC sp_releaseapplock @Resource = 'docusearch:routine:RESUMEN_DIARIO', @LockOwner = 'Session'", holder))
            await release.ExecuteNonQueryAsync();
        Assert(await routines.RunNowAsync(RoutineSchedule.DailySummary) is { Status: "OK" });
    });
    await Check("SQL Server: la pestaña carga los vectores por página solo bajo demanda y con permisos", async () =>
    {
        var docs = (await service.LoadAsync()).Documents;
        await service.SwitchProfileAsync(consultant.Id);
        var consultantIds = (await service.LoadAsync()).Documents.Select(d => d.Id).ToHashSet();
        await service.SwitchProfileAsync(admin.Id);
        var open = docs.First(d => consultantIds.Contains(d.Id));
        var secret = docs.First(d => !consultantIds.Contains(d.Id));
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.PageEmbeddings.Where(e => e.DocumentId == open.Id || e.DocumentId == secret.Id).ExecuteDeleteAsync();
            db.PageEmbeddings.AddRange(
                new PageEmbedding { DocumentId = open.Id, PageNumber = 1, Model = "fake", Dimensions = 2, Vector = EmbeddingIndexer.ToBytes([1f, 0f]) },
                new PageEmbedding { DocumentId = secret.Id, PageNumber = 1, Model = "fake", Dimensions = 2, Vector = EmbeddingIndexer.ToBytes([0f, 1f]) });
            await db.SaveChangesAsync();
        }
        try
        {
            var state = new WorkspaceState(service, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceState>.Instance);
            await state.RefreshAsync();
            Assert(state.Data!.Documents.All(d => d.PageVectors.Count == 0), "La instantánea de la pestaña no trae vectores.");
            Assert(state.Data.Users.Count > 0 && state.Data.Departments.Count > 0, "La pestaña conserva el directorio.");
            await state.EnsurePageVectorsAsync();
            Assert(state.Data.Documents.Single(d => d.Id == open.Id).PageVectors.ContainsKey(1), "Se adjuntan bajo demanda.");

            // Documentos ajenos al perfil actual no reciben vectores aunque se pasen explícitamente.
            await service.SwitchProfileAsync(consultant.Id);
            var foreign = new List<DocumentItem> { new() { Id = secret.Id, Confidentiality = secret.Confidentiality, Department = secret.Department } };
            await service.AttachPageVectorsAsync(foreign);
            Assert(foreign[0].PageVectors.Count == 0, "Sin permiso de lectura no se adjuntan vectores.");
        }
        finally
        {
            await service.SwitchProfileAsync(admin.Id);
            await using var db = await factory.CreateDbContextAsync();
            await db.PageEmbeddings.Where(e => e.DocumentId == open.Id || e.DocumentId == secret.Id).ExecuteDeleteAsync();
        }
    });
    // Al final de la suite: reemplaza los fragmentos de dos documentos con datos sintéticos.
    await Check("SQL Server + RAG: la caché de vectores da los mismos resultados y detecta cambios", async () =>
    {
        var docs = (await service.LoadAsync()).Documents.Take(2).ToList();
        Assert(docs.Count == 2, "Hacen falta dos documentos.");
        var random = new Random(42);
        float[] Vector(int dims = 8) => Enumerable.Range(0, dims).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        RagChunkVector Chunk(DocumentItem doc, int index, float[] vector, string model = "fake") =>
            new(new RagChunk($"{doc.Id}:cache:{index}", doc.Id, doc.InternalCode, "sql", 1, index, 0, 10, $"texto {index}", $"h{index}"),
                vector, model);
        var plain = new SqlRagVectorStore(factory);
        var cache = new RagVectorCache();
        var cached = new SqlRagVectorStore(factory, cache: cache);
        await plain.ReplaceDocumentAsync(docs[0].Id, Enumerable.Range(0, 12).Select(i => Chunk(docs[0], i, Vector())).ToList());
        // Incluye un fragmento de otras dimensiones: ambos caminos deben ignorarlo.
        await plain.ReplaceDocumentAsync(docs[1].Id,
            [.. Enumerable.Range(0, 11).Select(i => Chunk(docs[1], i, Vector())), Chunk(docs[1], 11, Vector(4))]);
        var ids = docs.Select(d => d.Id).ToArray();

        for (var round = 0; round < 5; round++)
        {
            var query = Vector();
            var expected = await plain.QueryAsync(query, ids, 7);
            var actual = await cached.QueryAsync(query, ids, 7);
            Assert(expected.Select(x => x.Chunk.Id).SequenceEqual(actual.Select(x => x.Chunk.Id)), $"Mismo orden (ronda {round}).");
            Assert(expected.Zip(actual).All(pair => Math.Abs(pair.First.Score - pair.Second.Score) < 1e-5), "Misma similitud.");
            Assert(expected.Zip(actual).All(pair => pair.First.Chunk == pair.Second.Chunk), "Mismo texto y metadatos.");
        }
        Assert(cache.Reloads == 1, "Sin cambios en la tabla no se recarga.");
        Assert((await cached.QueryAsync(Vector(), [docs[0].Id], 50)).All(x => x.Chunk.DocumentId == docs[0].Id), "Filtra por documento.");

        // Cambio hecho por otra instancia (sin caché), como haría --rag-reindex desde otro proceso.
        var fresh = Vector();
        await plain.ReplaceDocumentAsync(docs[0].Id, [Chunk(docs[0], 0, fresh)]);
        var top = await cached.QueryAsync(fresh, [docs[0].Id], 5);
        Assert(top.Count == 1 && top[0].Score > 0.999, "Ve el reemplazo hecho fuera de su instancia.");
        await plain.DeleteDocumentAsync(docs[1].Id);
        Assert((await cached.QueryAsync(Vector(), [docs[1].Id], 5)).Count == 0, "Ve el borrado hecho fuera de su instancia.");
        Assert(cache.Reloads == 3, "Una recarga por cada cambio detectado.");
        await plain.DeleteDocumentAsync(docs[0].Id);
        Assert((await cached.QueryAsync(Vector(), ids, 5)).Count == 0, "Tabla vacía para esos documentos.");
    });
    await Check("SQL Server + RAG: la búsqueda híbrida encuentra coincidencias literales que el vector no trae", async () =>
    {
        var doc = (await service.LoadAsync()).Documents.First();
        float[] Axis(int index) { var vector = new float[8]; vector[index] = 1; return vector; }
        var random = new Random(7);
        float[] Near() { var vector = Axis(0); for (var i = 1; i < 8; i++) vector[i] = (float)(random.NextDouble() * 0.5); return vector; }
        RagChunkVector Chunk(int index, string text, float[] vector) =>
            new(new RagChunk($"{doc.Id}:hyb:{index}", doc.Id, doc.InternalCode, "sql", 1, index, 0, text.Length, text, $"h{index}"), vector, "fake");
        var chunks = Enumerable.Range(0, 12).Select(i => Chunk(i, $"Texto general número {i} sobre servicios y condiciones.", Near())).ToList();
        // El dato buscado solo coincide literalmente: su vector es ortogonal a la consulta.
        chunks.Add(Chunk(12, "El expediente ZX-4471 fija una penalización del 3 % mensual.", Axis(1)));
        var store = new SqlRagVectorStore(factory, cache: new RagVectorCache());
        await store.ReplaceDocumentAsync(doc.Id, chunks);
        try
        {
            var settings = Options.Create(new RagOptions { CandidateK = 5, TopK = 8, Rerank = false });
            var noRerank = new ReversingReranker();
            const string question = "¿Qué penalización fija ZX-4471?";
            var before = await new HybridRagRetriever(store, settings, noRerank).RetrieveAsync(question, Axis(0), [doc.Id]);
            Assert(before.All(hit => hit.Chunk.ChunkIndex != 12), "Sin índice léxico, el fragmento literal no entra (comportamiento anterior)");
            var hybrid = new HybridRagRetriever(store, settings, noRerank, new RagLexicalIndex(), factory);
            var after = await hybrid.RetrieveAsync(question, Axis(0), [doc.Id]);
            Assert(after.Any(hit => hit.Chunk.ChunkIndex == 12), "Con índice léxico, el fragmento literal se recupera");
            Assert((await hybrid.RetrieveAsync(question, Axis(0), ["otro-documento"])).Count == 0, "Respeta los documentos legibles");
        }
        finally
        {
            await store.DeleteDocumentAsync(doc.Id);
        }
    });
}
else Console.WriteLine("INFO Integración SQL Server no ejecutada. Usa --database con DOCUSEARCH_TEST_CONNECTION.");

Console.WriteLine($"\n{passed} pruebas correctas; {failed} fallidas.");
return failed == 0 ? 0 : 1;

internal sealed class TestFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);
    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
}

internal sealed class TestDbException : System.Data.Common.DbException;

internal sealed class MapServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> services = [];
    public void Add<T>(T service) where T : notnull => services[typeof(T)] = service;
    public object? GetService(Type serviceType) => services.GetValueOrDefault(serviceType);
}

internal sealed class FailingFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => throw new InvalidOperationException("Base de datos no disponible.");
}

internal sealed class FakeAiGovernance : IAiGovernance
{
    public bool Block { get; init; }
    public long LastEstimate { get; private set; }
    public List<string> Events { get; } = [];
    public List<AiUsageRecord> Records { get; } = [];
    public AiPricing ResolvePricing(string engine, string provider, string model, AssistantProfile? profile) => AiPricing.Free;
    public Task AssertCanSpendAsync(string feature, long estimateMicroUsd, CancellationToken cancellationToken = default)
    {
        Events.Add("assert");
        LastEstimate = estimateMicroUsd;
        if (Block) throw new AiQuotaExceededException("Presupuesto mensual de IA agotado.", 1, 1, AiBudgetIncident.HardStop);
        return Task.CompletedTask;
    }
    public Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        Events.Add("record");
        Records.Add(record);
        return Task.CompletedTask;
    }
}

internal sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeRagEmbedder(Func<string, float[]> embed) : IRagEmbedder
{
    public int BatchCalls { get; private set; }
    public int SingleCalls { get; private set; }
    public bool Enabled => true;
    public string Model => "fake";
    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        SingleCalls++;
        return Task.FromResult(embed(text));
    }
    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        BatchCalls++;
        return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(embed).ToList());
    }
}

/// <summary>Retriever fijo; <paramref name="during"/> se ejecuta en mitad de la recuperación (p. ej. revocar permisos).</summary>
internal sealed class FakeRagRetriever(IReadOnlyList<RagHit> hits, Func<Task>? during = null) : IRagRetriever
{
    public async Task<IReadOnlyList<RagHit>> RetrieveAsync(string query, float[] queryVector,
        IReadOnlyCollection<string> documentIds, CancellationToken cancellationToken = default)
    {
        if (during is not null) await during();
        return hits.Where(hit => documentIds.Contains(hit.Chunk.DocumentId)).ToList();
    }
}

internal sealed class FakeRagStore(IReadOnlyList<RagScoredChunk> results) : IRagVectorStore
{
    public string Name => "fake";
    public Task UpsertAsync(IReadOnlyList<RagChunkVector> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ReplaceDocumentAsync(string documentId, IReadOnlyList<RagChunkVector> chunks,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<IReadOnlyList<RagScoredChunk>> QueryAsync(float[] vector, IReadOnlyCollection<string> documentIds,
        int topK, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RagScoredChunk>>(results.Take(topK).ToList());
    public Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class RecordingRagStore : IRagVectorStore
{
    public List<(string DocumentId, IReadOnlyList<RagChunkVector> Chunks)> Replaced { get; } = [];
    public string Name => "recording";
    public Task UpsertAsync(IReadOnlyList<RagChunkVector> chunks, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("La ingesta debe usar ReplaceDocumentAsync.");
    public Task ReplaceDocumentAsync(string documentId, IReadOnlyList<RagChunkVector> chunks,
        CancellationToken cancellationToken = default)
    {
        Replaced.Add((documentId, chunks));
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<RagScoredChunk>> QueryAsync(float[] vector, IReadOnlyCollection<string> documentIds,
        int topK, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<RagScoredChunk>>([]);
    public Task DeleteDocumentAsync(string documentId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("La ingesta no debe borrar por separado.");
}

/// <summary>Invierte el orden recibido para comprobar qué candidatos llegan al reranker.</summary>
internal sealed class ReversingReranker : IRagReranker
{
    public int Received { get; private set; }
    public Task<IReadOnlyList<RagHit>> RerankAsync(string query, IReadOnlyList<RagHit> candidates,
        CancellationToken cancellationToken = default)
    {
        Received = candidates.Count;
        return Task.FromResult<IReadOnlyList<RagHit>>(candidates.Reverse()
            .Select((hit, index) => new RagHit(hit.Chunk, 1 - index * 0.01)).ToList());
    }
}
internal sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}


internal sealed class FakeLangGraphRunner : ILangGraphRunner
{
    public string Response { get; set; } = "{}";
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public string? LastRequest { get; private set; }

    public Task<string> RunAsync(string pythonExecutable, string scriptFile, int timeoutSeconds,
        object? request = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = JsonSerializer.Serialize(request);
        return Failure is null ? Task.FromResult(Response) : Task.FromException<string>(Failure);
    }
}

/// <summary>IOptionsMonitor fijo para probar validadores que leen otra sección.</summary>
internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}

/// <summary>Fábrica HTTP cuyo cliente siempre falla: simula el cross-encoder caído.</summary>
internal sealed class FailingHttpFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new TestHttpHandler((_, _) =>
        throw new HttpRequestException("servicio caído"))) { BaseAddress = new Uri("http://127.0.0.1:9/") };
}
