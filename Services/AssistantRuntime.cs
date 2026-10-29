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
}

public sealed class AssistantOptions
{
    public string DefaultProfile { get; set; } = "";
    public List<AssistantProfile> Profiles { get; set; } = [];
}

/// <summary>
/// Estado en memoria del motor elegido por el administrador desde la interfaz.
/// El valor inicial procede de <c>Assistant:DefaultProfile</c> y se mantiene mientras
/// viva la aplicación; no se escribe en disco ni en la base de datos.
/// </summary>
public sealed class AssistantRuntime(IOptions<AssistantOptions> options, ILogger<AssistantRuntime> logger)
{
    private readonly object gate = new();
    private readonly AssistantOptions settings = options.Value;
    private string activeId = options.Value.DefaultProfile;

    public IReadOnlyList<AssistantProfile> Profiles => settings.Profiles;

    public AssistantProfile Active
    {
        get
        {
            lock (gate)
            {
                return settings.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, activeId, StringComparison.OrdinalIgnoreCase))
                    ?? settings.Profiles.FirstOrDefault()
                    ?? new AssistantProfile { Id = "extractive", Label = "Sin LLM (reglas)", Engine = "extractive" };
            }
        }
    }

    public event Action? Changed;

    public bool TrySelect(string? id, out AssistantProfile? profile)
    {
        profile = settings.Profiles.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return false;
        lock (gate) activeId = profile.Id;
        logger.LogInformation("Motor del asistente cambiado a {Profile} ({Engine} {Provider}/{Model}).",
            profile.Id, profile.Engine, profile.ProviderId, profile.ModelId);
        Changed?.Invoke();
        return true;
    }

    public static bool IsRemote(AssistantProfile profile) =>
        string.Equals(profile.Engine, "opencode", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(profile.ProviderId, "ollama", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(profile.ProviderId, "lmstudio", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(profile.ProviderId, "llamacpp", StringComparison.OrdinalIgnoreCase);

    public string Describe(AssistantProfile profile) => profile.Engine.ToLowerInvariant() switch
    {
        "opencode" => $"OpenCode · {profile.ProviderId}/{profile.ModelId}" + (IsRemote(profile) ? " · remoto" : " · local"),
        "ollama" => "Ollama · LangChain local",
        _ => "Sin LLM · extractivo"
    };
}
