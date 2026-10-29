using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DocuSearch.Services;

public static class LangChainRunner
{
    public static async Task<string> RunAsync(OllamaOptions options, object? request = null)
    {
        var executable = Environment.ExpandEnvironmentVariables(options.PythonExecutable);
        var script = Path.Combine(AppContext.BaseDirectory, "LangChain", "worker.py");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !File.Exists(script))
            throw new InvalidOperationException("No se encontró Python de LangChain o su worker. Ejecuta LangChain\\setup.ps1 y publica nuevamente la aplicación.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(script);
        if (request is null) start.ArgumentList.Add("--check");
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "LOCALAPPDATA" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["LANGSMITH_TRACING"] = "false";
        start.Environment["LANGCHAIN_TRACING_V2"] = "false";
        using var process = new Process { StartInfo = start };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var started = false;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("No se pudo iniciar LangChain.");
            started = true;
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, timeout.Token);
            if (request is not null)
                await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            var pending = new HashSet<Task> { output, error, process.WaitForExitAsync(timeout.Token) };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            var result = await output;
            var details = await error;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"LangChain no completó la consulta (código {process.ExitCode}). Comprueba Python, las dependencias y Ollama. {details.Trim()}");
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("LangChain terminó sin respuesta.");
            return result;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("No se pudo ejecutar Python para LangChain. Comprueba los permisos del ejecutable.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException($"LangChain superó el tiempo de espera de {options.TimeoutSeconds} segundos. No se utilizó ningún proveedor remoto.", ex);
        }
        finally
        {
            await timeout.CancelAsync();
            if (started && !process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (result.Length + count > 100000)
                throw new InvalidOperationException("LangChain superó el límite de tamaño de respuesta.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
