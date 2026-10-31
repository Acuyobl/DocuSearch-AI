using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

/// <summary>Motor y modelo que el asistente usará para generar respuestas.</summary>
public sealed class AssistantProfile
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>opencode | ollama | extractive</summary>
    public string Engine { get; set; } = "opencode";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Agent { get; set; } = "";
    /// <summary>Agrupación para la interfaz: Locales, Gratis, Sin LLM.</summary>
    public string Group { get; set; } = "Gratis";
    /// <summary>El proveedor local está configurado y conectado en OpenCode.</summary>
    public bool Available { get; set; } = true;
}

public sealed class AssistantOptions
{
    /// <summary>Id preferido (p. ej. "paid:deepseek/deepseek-flash" o "free:opencode/…").</summary>
    public string DefaultProfile { get; set; } = "";
    public int MaxFreeModels { get; set; } = 40;
    public int MaxLocalModels { get; set; } = 20;
    /// <summary>Muestra los modelos locales de OpenCode; desactivado por defecto.</summary>
    public bool IncludeLocalModels { get; set; }
    /// <summary>Máximo de modelos de pago listados por proveedor conectado (0 = ocultar).</summary>
    public int MaxPaidModels { get; set; } = 10;
}

/// <summary>
/// Catálogo dinámico de motores: consulta el servidor OpenCode de cada equipo y
/// publica dos grupos, <b>Locales</b> (proveedores locales como Ollama/LM Studio)
/// y <b>Gratis</b> (modelos con coste 0 de proveedores conectados). Si OpenCode no
/// está disponible, el asistente queda en modo extractivo. No hay modelos
/// hardcodeados: la lista depende de la configuración de OpenCode de cada usuario.
/// </summary>
public sealed class AssistantRuntime(IOptions<AssistantOptions> options, OpenCodeClient openCode,
    ILogger<AssistantRuntime> logger, OllamaClient? ollama = null)
{
    private static readonly string[] LocalProviders = ["ollama", "lmstudio", "llamacpp"];
    private static readonly string[] PreferredFreeModels = ["deepseek-v4.1-flash-free", "nemotron-3.5-lightning-free"];
    private static readonly string[] PreferredPaidModels = ["deepseek-flash", "deepseek-v4-pro"];

    private readonly AssistantOptions settings = options.Value;
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly object state = new();
    private List<AssistantProfile> profiles = [ExtractiveProfile()];
    private string activeId = "extractive";
    private DateTimeOffset loadedAt = DateTimeOffset.MinValue;
    private string? lastError;
    private bool userSelected;

    public event Action? Changed;

    public IReadOnlyList<AssistantProfile> Profiles
    {
        get { lock (state) return profiles; }
    }

    public AssistantProfile Active
    {
        get
        {
            lock (state)
            {
                return profiles.FirstOrDefault(profile => string.Equals(profile.Id, activeId, StringComparison.OrdinalIgnoreCase))
                    ?? profiles.FirstOrDefault()
                    ?? ExtractiveProfile();
            }
        }
    }

    public string? LastError
    {
        get { lock (state) return lastError; }
    }

    public DateTimeOffset LoadedAt
    {
        get { lock (state) return loadedAt; }
    }

    public static bool IsRemote(AssistantProfile profile) => IsRemoteProvider(profile);

    private static bool IsRemoteProvider(AssistantProfile profile) =>
        string.Equals(profile.Engine, "opencode", StringComparison.OrdinalIgnoreCase) &&
        !LocalProviders.Contains(profile.ProviderId, StringComparer.OrdinalIgnoreCase);

    public async Task EnsureLoadedAsync()
    {
        if (NeedsRefresh()) await RefreshAsync();
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (!force && !NeedsRefresh()) return;
        await refreshGate.WaitAsync();
        try
        {
            if (!force && !NeedsRefresh()) return;
            var built = new List<AssistantProfile>();
            string? error = null;
            if (openCode.Enabled)
            {
                try
                {
                    built.AddRange(BuildProfiles(await openCode.FetchCatalogAsync()));
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    logger.LogWarning(ex, "No se pudo consultar el catálogo de OpenCode.");
                    error = "No se pudo consultar OpenCode (" + ex.Message + ").";
                    if (Profiles.Any(profile => profile.Engine != "extractive"))
                    {
                        lock (state)
                        {
                            loadedAt = DateTimeOffset.UtcNow;
                            lastError = error + " Se mantiene el catálogo anterior; pulsa «Actualizar motores» para reintentar.";
                        }
                        Changed?.Invoke();
                        return;
                    }
                }
            }
            else if (ollama is not { Enabled: true })
            {
                error = "OpenCode y Ollama están desactivados; solo está disponible el modo extractivo.";
            }

            if (settings.IncludeLocalModels && ollama is { Enabled: true })
                built.Add(await BuildOllamaProfileAsync());

            built.Add(ExtractiveProfile());
            built = built
                .OrderBy(profile => profile.Group == "Locales" ? 0 : profile.Group == "Gratis" ? 1 : 2)
                .ThenBy(profile => profile.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var hasEngine = built.Any(profile => profile.Engine != "extractive");
            Apply(built, hasEngine ? null : error);
            logger.LogInformation("Catálogo del asistente actualizado: {Local} local(es), {Free} gratis, LangChain disponible en la lista si está activo.",
                built.Count(profile => profile.Group == "Locales"), built.Count(profile => profile.Group == "Gratis"));
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task<AssistantProfile> BuildOllamaProfileAsync()
    {
        var available = await ollama!.IsReachableAsync();
        var label = ollama.UseLangChain ? $"LangChain · Ollama local · {ollama.Model}" : $"Ollama directo · {ollama.Model}";
        return new AssistantProfile
        {
            Id = "ollama:local",
            Label = label + (available ? "" : " (sin conexión)"),
            Engine = "ollama",
            ProviderId = "ollama",
            ModelId = ollama.Model,
            Group = "Locales",
            Available = available
        };
    }

    public bool TrySelect(string? id, out AssistantProfile? profile)
    {
        profile = Profiles.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return false;
        lock (state) activeId = profile.Id;
        userSelected = true;
        logger.LogInformation("Motor del asistente cambiado a {Profile} ({Engine} {Provider}/{Model}).",
            profile.Id, profile.Engine, profile.ProviderId, profile.ModelId);
        Changed?.Invoke();
        return true;
    }

    public string Describe(AssistantProfile profile) => profile.Engine.ToLowerInvariant() switch
    {
        "opencode" => (openCode.UseLangChain ? "LangChain · " : "") + $"OpenCode · {profile.ProviderId}/{profile.ModelId}" + (IsRemote(profile) ? " · remoto" : " · local"),
        "ollama" => "Ollama · LangChain local",
        _ => "Sin LLM · extractivo"
    };

    private bool NeedsRefresh() => loadedAt == DateTimeOffset.MinValue || DateTimeOffset.UtcNow - loadedAt > TimeSpan.FromSeconds(60);

    private List<AssistantProfile> BuildProfiles(OpenCodeCatalog catalog)
    {
        var connected = catalog.Connected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<AssistantProfile>();
        foreach (var provider in catalog.Providers)
        {
            var isLocal = IsLocalProvider(provider);
            if (isLocal && !settings.IncludeLocalModels) continue;
            var isConnected = connected.Contains(provider.Id);
            if (!isLocal && !isConnected) continue;
            var active = provider.Models
                .Where(model => !string.Equals(model.Status, "disabled", StringComparison.OrdinalIgnoreCase));
            var models = isLocal
                ? active.Take(settings.MaxLocalModels)
                : active.Where(model => model.InputCost == 0 && model.OutputCost == 0).Take(settings.MaxFreeModels)
                    .Concat(active.Where(model => model.InputCost > 0 || model.OutputCost > 0).Take(settings.MaxPaidModels));
            foreach (var model in models)
            {
                var free = model.InputCost == 0 && model.OutputCost == 0;
                var group = isLocal ? "Locales" : free ? "Gratis" : "Pago";
                var prefix = isLocal ? "local" : free ? "free" : "paid";
                result.Add(new AssistantProfile
                {
                    Id = $"{prefix}:{provider.Id}/{model.Id}",
                    Label = (isLocal
                        ? $"Local · {provider.Name} · {model.Name ?? model.Id}" + (isConnected ? "" : " (sin conexión)")
                        : $"{(free ? "Gratis" : "Pago")} · {provider.Name} · {model.Name ?? model.Id}")
                        + (openCode.UseLangChain ? " · LangChain" : ""),
                    Engine = "opencode",
                    ProviderId = provider.Id,
                    ModelId = model.Id,
                    Group = group,
                    Available = isConnected
                });
            }
        }
        result = result
            .OrderBy(profile => profile.Group == "Locales" ? 0 : profile.Group == "Pago" ? 1 : 2)
            .ThenBy(profile => profile.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    private void Apply(List<AssistantProfile> candidates, string? error)
    {
        lock (state)
        {
            profiles = candidates;
            loadedAt = DateTimeOffset.UtcNow;
            lastError = error;
            if (!userSelected || profiles.All(profile => !string.Equals(profile.Id, activeId, StringComparison.OrdinalIgnoreCase)))
                activeId = ChooseDefault(candidates)?.Id ?? "extractive";
        }
        Changed?.Invoke();
    }

    private AssistantProfile? ChooseDefault(List<AssistantProfile> candidates)
    {
        if (!string.IsNullOrWhiteSpace(settings.DefaultProfile))
        {
            var preferred = candidates.FirstOrDefault(profile => string.Equals(profile.Id, settings.DefaultProfile, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null) return preferred;
        }
        // Prioridad: modelo de pago configurado (DeepSeek), luego gratis y, por último,
        // locales solo si se muestran. Los gratis pueden agotar cuota en el proveedor.
        foreach (var name in PreferredPaidModels)
            foreach (var profile in candidates.Where(profile => profile.Group == "Pago"))
                if (profile.ModelId.Contains(name, StringComparison.OrdinalIgnoreCase)) return profile;
        var paid = candidates.FirstOrDefault(profile => profile.Group == "Pago");
        if (paid is not null) return paid;
        foreach (var name in PreferredFreeModels)
            foreach (var profile in candidates.Where(profile => profile.Group == "Gratis"))
                if (profile.ModelId.Contains(name, StringComparison.OrdinalIgnoreCase)) return profile;
        var free = candidates.FirstOrDefault(profile => profile.Group == "Gratis");
        if (free is not null) return free;
        var connectedLocal = candidates.FirstOrDefault(profile => profile.Group == "Locales" && profile.Available);
        if (connectedLocal is not null) return connectedLocal;
        return candidates.FirstOrDefault(profile => profile.Group == "Locales");
    }

    private static bool IsLocalProvider(OpenCodeProvider provider)
    {
        if (LocalProviders.Contains(provider.Id, StringComparer.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(provider.BaseUrl) || !Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase);
    }

    private static AssistantProfile ExtractiveProfile() => new()
    {
        Id = "extractive", Label = "Sin LLM · reglas (extractivo)", Engine = "extractive", Group = "Sin LLM"
    };
}
