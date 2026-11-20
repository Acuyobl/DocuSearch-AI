using DocuSearch.Data;
using DocuSearch.Services;
using DocuSearch.Services.Mcp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var connection = Environment.GetEnvironmentVariable("DOCUSEARCH_MCP_CONNECTION")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__Documents");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException(
        "Configura DOCUSEARCH_MCP_CONNECTION o ConnectionStrings__Documents para el servidor MCP.");

var builder = Host.CreateApplicationBuilder(args);

// stdout está reservado al protocolo JSON-RPC; los logs van a stderr.
builder.Logging.ClearProviders();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddDbContextFactory<AppDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddScoped<LocalSession>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<PdfProcessor>();
builder.Services.AddScoped<DocumentService>(services => new DocumentService(
    services.GetRequiredService<IDbContextFactory<AppDbContext>>(),
    services.GetRequiredService<LocalSession>(),
    services.GetRequiredService<PdfProcessor>()));
builder.Services.AddScoped<McpIdentity>();
builder.Services.AddScoped<ReadOnlyDocumentTools>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(ReadOnlyDocumentTools).Assembly);

await builder.Build().RunAsync();
