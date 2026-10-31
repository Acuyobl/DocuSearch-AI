using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocuSearch.Data;
using DocuSearch.Models;
using DocuSearch.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using System.Net;
using Microsoft.Extensions.Options;

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
OllamaClient Ollama(HttpClient http, bool enabled = true) => new(http, Options.Create(new OllamaOptions { Enabled = enabled }));
OpenCodeClient OpenCodeFor(HttpClient http, bool enabled = true, string password = "secreto", string provider = "ollama", string model = "qwen3:4b", string agent = "zodiac-asistente") =>
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
        var body = path switch
        {
            "/global/health" => """{"healthy":true,"version":"1.18.32"}""",
            "/agent" => """[{"name":"build"},{"name":"zodiac-asistente"}]""",
            "/experimental/tool/ids" => """["bash","read"]""",
            "/session" => """{"id":"ses_test"}""",
            "/session/ses_test/message" => "{\"info\":{\"tokens\":{\"input\":10,\"output\":5}},\"parts\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(answerText) + "}]}",
            _ => "{}"
        };
        return Task.FromResult(JsonResponse(body));
    }));
HttpClient OpenCodeCatalogMock(string providerJson) =>
    new(new TestHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = path switch
        {
            "/global/health" => """{"healthy":true,"version":"1.18.32"}""",
            "/agent" => """[{"name":"build"}]""",
            "/provider" => providerJson,
            "/experimental/tool/ids" => """["bash","read"]""",
            _ => "{}"
        };
        return Task.FromResult(JsonResponse(body));
    }));
const string catalogJson = """
    {"all":[
      {"id":"opencode","name":"OpenCode","options":{"baseURL":""},"models":{
        "free-model":{"id":"free-model","name":"Free Model","cost":{"input":0,"output":0},"status":"active"},
        "paid-model":{"id":"paid-model","name":"Paid Model","cost":{"input":1,"output":2},"status":"active"}}},
      {"id":"ollama","name":"Ollama (local)","options":{"baseURL":"http://127.0.0.1:11434/v1"},"models":{
        "qwen3:4b":{"id":"qwen3:4b","name":"Qwen3 4B","cost":{"input":0,"output":0},"status":"active"}}},
      {"id":"lmstudio","name":"LM Studio","options":{"baseURL":"http://localhost:1234/v1"},"models":{
        "local-model":{"id":"local-model","name":"Local Model","cost":{"input":0,"output":0},"status":"active"}}},
      {"id":"deepseek","name":"DeepSeek","options":{"baseURL":""},"models":{
        "deepseek-flash":{"id":"deepseek-flash","name":"DeepSeek Flash","cost":{"input":1,"output":2},"status":"active"}}}
    ],"connected":["opencode","ollama"],"default":{}}
    """;
const string localModelJson = """{"details":{"format":"gguf"},"capabilities":["completion"]}""";
string ChatJson(string content, bool done = true, string reason = "stop") =>
    JsonSerializer.Serialize(new { done, done_reason = reason, message = new { content } });
HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

await Check("Cinco expedientes, tres perfiles y seis departamentos preservados", () => Sync(() =>
    Assert(seed.Documents.Count == 5 && seed.Users.Count == 3 && seed.Departments.Count == 6)));
await Check("Los perfiles del seed coinciden con las cuentas TOTP configuradas", () => Sync(() =>
{
    var emails = seed.Users.Select(u => u.Email).OrderBy(e => e, StringComparer.Ordinal).ToArray();
    Assert(emails.SequenceEqual(new[]
    {
        "acuyobl@gmail.com", "carloshm7@gmail.com", "hubertaf@gmail.com"
    }));
    Assert(seed.Users.All(u => u.Status == "Activo"));
}));
await Check("Páginas y notas de ejemplo preservadas", () => Sync(() =>
    Assert(seed.Documents.Sum(d => d.PageCount) == 124 && seed.Documents.Sum(d => d.Pages.Count) == 10 && seed.Documents.Sum(d => d.Notes.Count) == 2)));
await Check("Ejemplos no inventan archivos PDF originales", () => Sync(() => Assert(seed.Documents.All(d => !d.HasOriginal))));
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

await Check("Ollama: listas, páginas y entidades conservan su respuesta exacta", () => Sync(() =>
{
    Assert(new[] { "Lista documentos internos", "Página 4 de CNT-2025-0892", "Extrae montos", "Autor de POL-2025-0012", "CNT-2025-0892" }.All(LocalAgent.IsExactRequest));
    Assert(!LocalAgent.IsExactRequest("Resume POL-2025-0012") && !LocalAgent.IsExactRequest("¿Qué SLA garantiza el contrato?"));
}));
await Check("Ollama: un fallo libera la interfaz y muestra el error", async () =>
{
    var factory = new TestFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer("Server=localhost;Database=offline;Integrated Security=True", sql => sql.UseCompatibilityLevel(150)).Options);
    var state = new WorkspaceState(new DocumentService(factory, Session(), processor), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceState>.Instance);
    using var http = new HttpClient(new TestHttpHandler((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException())));
    await state.RunAsync(() => Ollama(http).CheckAsync());
    Assert(!state.Busy && state.Notice is null && state.Error!.Contains("Ollama local"));
});
await Check("Ollama: bloquea servidores externos, credenciales y modelos cloud", () => Sync(() =>
{
    foreach (var url in new[] { "https://ollama.com", "http://192.168.1.1:11434", "http://localhost:11434", "http://user:pass@127.0.0.1:11434", "http://127.0.0.1:11434/remote" })
    {
        using var http = new HttpClient();
        Throws<InvalidOperationException>(() => new OllamaClient(http, Options.Create(new OllamaOptions { BaseUrl = url })));
    }
    using var client = new HttpClient();
    Throws<InvalidOperationException>(() => new OllamaClient(client, Options.Create(new OllamaOptions { Model = "qwen:cloud" })));
}));
await Check("Ollama: rechaza metadatos remotos o no locales antes de enviar documentos", async () =>
{
    foreach (var metadata in new[]
    {
        """{"details":{"format":"gguf"},"capabilities":["completion"],"remote_host":"https://ollama.com"}""",
        """{"details":{"format":"gguf"},"capabilities":["completion"],"remote_model":"remote-name"}""",
        """{"details":{"format":""},"capabilities":["completion"]}""",
        """{"details":{"format":"gguf"},"capabilities":["embedding"]}"""
    })
    {
        var calls = 0;
        using var http = new HttpClient(new TestHttpHandler((request, _) => { calls++; Assert(request.RequestUri!.AbsolutePath == "/api/show"); return Task.FromResult(JsonResponse(metadata)); }));
        await ThrowsAsync<InvalidOperationException>(() => Ollama(http).GenerateAsync("Resumen", [new(contract, 1, "Texto autorizado", 100)]));
        Assert(calls == 1);
    }
});
await Check("Ollama: contexto acotado, sin herramientas y citas validadas", async () =>
{
    JsonElement payload = default;
    using var http = new HttpClient(new TestHttpHandler(async (request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/show") return JsonResponse(localModelJson);
        Assert(request.RequestUri.AbsolutePath == "/api/chat");
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        payload = json.RootElement.Clone();
        return JsonResponse(ChatJson("""{"answer":"Respuesta documentada","citations":[2,1,2]}"""));
    }));
    var sources = Enumerable.Range(1, 10).Select(page => new SearchHit(contract, page, new string('x', 3000), 100)).ToList();
    var result = await Ollama(http).GenerateAsync("Consulta", sources);
    Assert(result.Sources.Select(s => s.PageNumber).SequenceEqual([2, 1]) && result.Provider.Contains("Ollama local"));
    Assert(!payload.GetProperty("stream").GetBoolean() && !payload.TryGetProperty("tools", out _));
    var context = JsonDocument.Parse(payload.GetProperty("messages")[1].GetProperty("content").GetString()!);
    using (context)
    {
        var sent = context.RootElement.GetProperty("sources").EnumerateArray().ToList();
        Assert(sent.Count == 6 && sent.All(s => s.GetProperty("text").GetString()!.Length <= 1001));
        Assert(sent.All(s => s.GetProperty("code").GetString() == contract.InternalCode));
    }
});
await Check("Ollama: no acepta citas inventadas, respuesta vacía ni JSON incorrecto", async () =>
{
    foreach (var content in new[] { """{"answer":"Texto","citations":[0]}""", """{"answer":"Texto","citations":[2]}""", """{"answer":"Texto","citations":[]}""", """{"answer":"","citations":[1]}""", "not JSON" })
    {
        using var http = new HttpClient(new TestHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath == "/api/show" ? localModelJson : ChatJson(content)))));
        await ThrowsAsync<InvalidOperationException>(() => Ollama(http).GenerateAsync("Resumen", [new(contract, 1, "Texto autorizado", 100)]));
    }
});
await Check("Ollama: no genera respuestas sin fuentes ni llamadas al desactivarlo", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new TestHttpHandler((_, _) => { calls++; return Task.FromResult(JsonResponse(localModelJson)); }));
    await Ollama(http, false).CheckAsync();
    await ThrowsAsync<InvalidOperationException>(() => Ollama(http).GenerateAsync("Pregunta", []));
    Assert(calls == 0);
});
await Check("Ollama: errores HTTP, HTML y timeout se notifican sin fallback", async () =>
{
    foreach (var failure in new[] { "http", "html", "timeout", "connection" })
    {
        using var http = new HttpClient(new TestHttpHandler((_, _) => failure switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            "html" => Task.FromResult(JsonResponse("<html>No API</html>")),
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException()),
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException())
        }));
        await ThrowsAsync<InvalidOperationException>(() => Ollama(http).CheckAsync());
    }
});
await Check("Ollama: respuestas truncadas o incompletas no parecen exitosas", async () =>
{
    foreach (var done in new[] { true, false })
    {
        using var http = new HttpClient(new TestHttpHandler((request, _) => Task.FromResult(JsonResponse(request.RequestUri!.AbsolutePath == "/api/show" ? localModelJson : ChatJson("""{"answer":"Texto","citations":[1]}""", done, "length")))));
        await ThrowsAsync<InvalidOperationException>(() => Ollama(http).GenerateAsync("Pregunta", [new(contract, 1, "Texto", 100)]));
    }
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
await Check("Observabilidad: tokens directos reportados, no estimados", async () =>
{
    using var http = new HttpClient(new TestHttpHandler((request, _) => Task.FromResult(JsonResponse(
        request.RequestUri!.AbsolutePath == "/api/show" ? localModelJson :
        """{"done":true,"done_reason":"stop","message":{"content":"{\"answer\":\"Test\",\"citations\":[1]}"},"prompt_eval_count":123,"eval_count":45}"""))));
    var answer = await Ollama(http).GenerateAsync("Pregunta", [new(contract, 1, "Texto", 100)]);
    Assert(answer.InputTokens == 123 && answer.OutputTokens == 45);
});
await Check("LangChain: Python ausente falla explícitamente, sin fallback", async () =>
{
    var options = new OllamaOptions { UseLangChain = true, PythonExecutable = @"C:\DocuSearch-missing-runtime\python.exe" };
    await ThrowsAsync<InvalidOperationException>(() => LangChainRunner.RunAsync(options));
});
if (args.Contains("--langchain"))
{
    await Check("LangChain real: worker disponible e identificado", async () =>
    {
        using var result = JsonDocument.Parse(await LangChainRunner.RunAsync(new OllamaOptions()));
        Assert(result.RootElement.GetProperty("framework").GetString() == "LangChain" &&
            result.RootElement.GetProperty("status").GetString() == "ready");
    });
    await Check("LangChain real: rechaza endpoint remoto antes de generar", async () =>
    {
        await ThrowsAsync<InvalidOperationException>(() => LangChainRunner.RunAsync(new OllamaOptions(), new
        {
            base_url = "https://ollama.com", model = "qwen2.5-coder:7b", timeout = 5,
            question = "Pregunta", sources = new[] { new { id = 1, code = "TEST", title = "Test", page = 1, text = "Test" } },
            system = "Only local"
        }));
    });
    await Check("LangChain real: C# a Python a Ollama con cita validada", async () =>
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var client = new OllamaClient(http, Options.Create(new OllamaOptions { Enabled = true, UseLangChain = true }));
        var document = new DocumentItem { InternalCode = "TEST-2026-LOCAL", Title = "Prueba sintética LangChain" };
        var answer = await client.GenerateAsync("¿Qué disponibilidad garantiza el contrato?",
            [new(document, 1, "El contrato garantiza una disponibilidad SLA de 99.99%.", 100)]);
        Assert(!answer.Text.Contains('\uFFFD') && (answer.Text.Contains("99.99") || answer.Text.Contains("99,99")) &&
            answer.Sources.Single().Document.Id == document.Id && answer.Provider.Contains("LangChain") &&
            answer.InputTokens > 0 && answer.OutputTokens > 0);
        Console.WriteLine($"INFO LangChain real: {answer.Provider}; {answer.Text}");
    });
}
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
            .Where(provider => provider.Id is "ollama" or "lmstudio" or "llamacpp")
            .SelectMany(provider => provider.Models).Count();
        var free = catalog.Providers
            .Where(provider => connected.Contains(provider.Id) && provider.Id is not ("ollama" or "lmstudio" or "llamacpp"))
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
if (args.Contains("--ollama"))
{
    await Check("Ollama real: modelo local genera respuesta con cita autorizada", async () =>
    {
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        var document = new DocumentItem { InternalCode = "TEST-2026-LOCAL", Title = "Prueba sintética local" };
        var answer = await Ollama(http).GenerateAsync("¿Qué disponibilidad garantiza el contrato?", [new(document, 1, "El contrato garantiza una disponibilidad SLA de 99.99%.", 100)]);
        Assert((answer.Text.Contains("99.99") || answer.Text.Contains("99,99")) && answer.Sources.Single().Document.Id == document.Id);
        Console.WriteLine($"INFO Modelo real: {answer.Provider}; respuesta: {answer.Text}");
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
    var uri = Totp.OtpAuthUri("DocuSearch AI", "acuyobl@gmail.com", "VL45YEQOYLCSP6TQHG563ULJZBMOO3ZG");
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
await Check("OpenCode: comprueba salud y agente configurado", async () =>
{
    var calls = new List<string>();
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}", request => calls.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}"));
    await OpenCodeFor(http).CheckAsync();
    Assert(calls.Contains("GET /global/health") && calls.Contains("GET /agent"));
});
await Check("OpenCode: genera respuesta con citas, tokens y autenticación", async () =>
{
    string? messageBody = null, auth = null;
    var calls = new List<string>();
    using var http = OpenCodeMock("{\"answer\":\"ok\",\"citations\":[1]}", request =>
    {
        calls.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
        if (request.RequestUri.AbsolutePath.EndsWith("/message", StringComparison.Ordinal))
        {
            messageBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            auth = request.Headers.Authorization?.ToString();
        }
    });
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    var answer = await OpenCodeFor(http).GenerateAsync("¿Qué SLA garantiza el contrato?", [new(document, 4, "SLA 99.99%", 100)]);
    Assert(answer.Text == "ok" && answer.Sources.Single().PageNumber == 4);
    Assert(answer.Provider.Contains("OpenCode", StringComparison.Ordinal) && answer.InputTokens == 10 && answer.OutputTokens == 5);
    Assert(messageBody is not null && messageBody.Contains("\"agent\":\"zodiac-asistente\"", StringComparison.Ordinal) &&
        messageBody.Contains("\"providerID\":\"ollama\"", StringComparison.Ordinal) &&
        messageBody.Contains("\"modelID\":\"qwen3:4b\"", StringComparison.Ordinal));
    Assert(messageBody!.Contains("SLA", StringComparison.Ordinal) &&
        messageBody.Contains("CNT-2025-0892", StringComparison.Ordinal));
    Assert(auth is not null && auth.StartsWith("Basic ", StringComparison.Ordinal));
    Assert(calls.Contains("DELETE /session/ses_test"));
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
        if (request.RequestUri!.AbsolutePath.EndsWith("/message", StringComparison.Ordinal))
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    });
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    var answer = await OpenCodeFor(http).GenerateAsync("q", [new(document, 1, "texto", 100)],
        "deepseek", "deepseek-flash", "zodiac-asistente");
    Assert(body is not null && body.Contains("\"providerID\":\"deepseek\"", StringComparison.Ordinal) &&
        body.Contains("\"modelID\":\"deepseek-flash\"", StringComparison.Ordinal));
    Assert(answer.Provider.Contains("deepseek/deepseek-flash", StringComparison.Ordinal));
});
await Check("Asistente: catálogo dinámico separa gratis y locales", async () =>
{
    var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(OpenCodeCatalogMock(catalogJson)),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
    await runtime.RefreshAsync(force: true);
    var locals = runtime.Profiles.Where(profile => profile.Group == "Locales").ToList();
    var free = runtime.Profiles.Where(profile => profile.Group == "Gratis").ToList();
    Assert(locals.Count == 2 && free.Count == 1);
    Assert(locals.Any(profile => profile.ProviderId == "ollama" && profile.Available));
    Assert(locals.Any(profile => profile.ProviderId == "lmstudio" && !profile.Available));
    Assert(free.Single().ModelId == "free-model" && AssistantRuntime.IsRemote(free.Single()));
    Assert(!runtime.Profiles.Any(profile => profile.ModelId == "paid-model"));
    Assert(runtime.Active.Group == "Gratis" && runtime.Active.ModelId == "free-model");
    Assert(runtime.TrySelect(free.Single().Id, out var selected) && selected!.ModelId == "free-model");
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
            "/global/health" => """{"healthy":true,"version":"1.18.32"}""",
            "/provider" => catalogJson,
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
        if (request.RequestUri!.AbsolutePath.EndsWith("/message", StringComparison.Ordinal))
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
    });
    var document = new DocumentItem { InternalCode = "CNT-2025-0892", Title = "Contrato" };
    await OpenCodeFor(http, agent: "").GenerateAsync("q", [new(document, 1, "texto", 100)]);
    Assert(body is not null && body.Contains("\"tools\":{\"bash\":false,\"read\":false}", StringComparison.Ordinal));
    Assert(!body!.Contains("\"agent\":", StringComparison.Ordinal));
});
await Check("Asistente: incluye LangChain/Ollama local en el catálogo", async () =>
{
    HttpClient OllamaMock() => new(new TestHttpHandler((request, _) =>
        Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal)
            ? JsonResponse("""{"models":[]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound))));
    using var ollamaHttp = OllamaMock();
    var ollamaClient = new OllamaClient(ollamaHttp, Options.Create(new OllamaOptions
    {
        Enabled = true, UseLangChain = true, BaseUrl = "http://127.0.0.1:11434", Model = "qwen3:4b"
    }));
    var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(OpenCodeCatalogMock(catalogJson)),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance, ollamaClient);
    await runtime.RefreshAsync(force: true);
    var langchain = runtime.Profiles.SingleOrDefault(profile => profile.Id == "ollama:local");
    Assert(langchain is not null && langchain.Engine == "ollama" && langchain.Group == "Locales" &&
        langchain.Available && langchain.Label.Contains("LangChain", StringComparison.Ordinal));

    using var offlineOllamaHttp = OllamaMock();
    var offlineOllama = new OllamaClient(offlineOllamaHttp, Options.Create(new OllamaOptions
    {
        Enabled = true, UseLangChain = true, BaseUrl = "http://127.0.0.1:11434", Model = "qwen3:4b"
    }));
    var withoutOpenCode = new AssistantRuntime(Options.Create(new AssistantOptions()), OpenCodeFor(new HttpClient(), enabled: false),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance, offlineOllama);
    await withoutOpenCode.RefreshAsync(force: true);
    Assert(withoutOpenCode.Profiles.Any(profile => profile.Engine == "ollama") &&
        withoutOpenCode.Profiles.Any(profile => profile.Engine == "extractive"));
});
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
    var agent = new LocalAgent(service, session, Ollama(offlineHttp, false), telemetry);
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
            if (path.EndsWith("/message", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return Task.FromResult(JsonResponse(path switch
            {
                "/global/health" => """{"healthy":true,"version":"1.18.32"}""",
                "/provider" => catalogJson,
                "/agent" => """[{"name":"zodiac-asistente"}]""",
                "/experimental/tool/ids" => """["bash","read"]""",
                "/session" => """{"id":"ses_fallback"}""",
                _ => "{}"
            }));
        }));
        var failing = OpenCodeFor(http);
        var runtime = new AssistantRuntime(Options.Create(new AssistantOptions()), failing,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AssistantRuntime>.Instance);
        await runtime.RefreshAsync(force: true);
        var fallbackAgent = new LocalAgent(service, session, Ollama(new HttpClient(), false), telemetry, failing, runtime,
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
    await Check("SQL Server + Ollama: solo se envían fuentes autorizadas al consultor", async () =>
    {
        await service.SwitchProfileAsync(consultant.Id);
        try
        {
            var readable = (await service.LoadAsync()).Documents.Select(d => d.InternalCode).ToHashSet();
            var generated = false;
            using var http = new HttpClient(new TestHttpHandler(async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/show") return JsonResponse(localModelJson);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                using var context = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                Assert(context.RootElement.GetProperty("sources").EnumerateArray().All(s => readable.Contains(s.GetProperty("code").GetString()!)));
                generated = true;
                return JsonResponse(ChatJson("""{"answer":"Resumen autorizado","citations":[1]}"""));
            }));
            var answer = await new LocalAgent(service, session, Ollama(http), telemetry).AskAsync("Resume POL-2025-0012");
            Assert(generated && answer.Provider.Contains("Ollama") && answer.Sources.All(s => readable.Contains(s.Document.InternalCode)));
        }
        finally { await service.SwitchProfileAsync(admin.Id); }
    });
    await Check("SQL Server + Ollama: revocación durante generación bloquea respuesta", async () =>
    {
        try
        {
            using var http = new HttpClient(new TestHttpHandler(async (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/show") return JsonResponse(localModelJson);
                await service.SwitchProfileAsync(consultant.Id);
                return JsonResponse(ChatJson("""{"answer":"Respuesta ahora revocada","citations":[1]}"""));
            }));
            await ThrowsAsync<UnauthorizedAccessException>(() => new LocalAgent(service, session, Ollama(http), telemetry).AskAsync("Resume FIN-2024-Q4-01"));
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
internal sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
