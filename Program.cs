using DocuSearch.Cli;
using DocuSearch.Components;
using DocuSearch.Hosting;
using DocuSearch.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddDocuSearch();

var app = builder.Build();
// Comandos de mantenimiento (--initialize-database, --rag-eval, --search…): no arrancan la web.
if (await CliCommands.TryRunAsync(app, args)) return;

app.UseDocuSearchPipeline();
app.MapDocuSearchAuth();
app.MapDocuSearchHealth();
app.MapDocuSearchApi();
app.MapGovernanceApi();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
