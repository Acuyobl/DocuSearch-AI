using DocuSearch.Models;
using DocuSearch.Services.Rag;
using Microsoft.Extensions.Options;

namespace DocuSearch.Services;

/// <summary>
/// Validación de configuración al arrancar. Las reglas solo rechazan valores sin sentido (que antes fallaban
/// en la primera consulta o degradaban en silencio); los valores por defecto y las plantillas las cumplen.
/// </summary>
public static class OptionsValidation
{
    public static IServiceCollection AddDocuSearchOptionsValidation(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<RagOptions>, RagOptionsValidator>();
        services.AddSingleton<IValidateOptions<EmbeddingOptions>, EmbeddingOptionsValidator>();
        services.AddSingleton<IValidateOptions<PineconeOptions>, PineconeOptionsValidator>();
        services.AddSingleton<IValidateOptions<ResilienceOptions>, ResilienceOptionsValidator>();
        services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
        services.AddOptions<RagOptions>().ValidateOnStart();
        services.AddOptions<EmbeddingOptions>().ValidateOnStart();
        services.AddOptions<PineconeOptions>().ValidateOnStart();
        services.AddOptions<ResilienceOptions>().ValidateOnStart();
        services.AddOptions<AuthOptions>().ValidateOnStart();
        return services;
    }

    internal static ValidateOptionsResult Result(List<string> errors) =>
        errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);

    internal static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public sealed class RagOptionsValidator(IOptionsMonitor<PineconeOptions> pinecone) : IValidateOptions<RagOptions>
{
    public ValidateOptionsResult Validate(string? name, RagOptions options)
    {
        var errors = new List<string>();
        if (options.ChunkingStrategy is not { } strategy ||
            !(strategy.Equals("fixed", StringComparison.OrdinalIgnoreCase) || strategy.Equals("semantic", StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Rag:ChunkingStrategy debe ser «fixed» o «semantic» (es «{options.ChunkingStrategy}»).");
        if (options.MaxTokens is < 16 or > 8192) errors.Add("Rag:MaxTokens debe estar entre 16 y 8192.");
        if (options.OverlapTokens < 0 || options.OverlapTokens >= options.MaxTokens)
            errors.Add("Rag:OverlapTokens debe ser mayor o igual que 0 y menor que Rag:MaxTokens.");
        if (options.SemanticBreakThreshold is < 0 or > 1) errors.Add("Rag:SemanticBreakThreshold debe estar entre 0 y 1.");
        if (options.CandidateK < 1) errors.Add("Rag:CandidateK debe ser al menos 1.");
        if (options.TopK < 1) errors.Add("Rag:TopK debe ser al menos 1.");
        if (options.VectorWeight is < 0 or > 1 || options.LexicalWeight is < 0 or > 1 ||
            options.VectorWeight + options.LexicalWeight <= 0)
            errors.Add("Rag:VectorWeight y Rag:LexicalWeight deben estar entre 0 y 1 y no ser ambos 0.");
        if (options.RerankCandidates < 1) errors.Add("Rag:RerankCandidates debe ser al menos 1.");
        if (options.RerankerTimeoutSeconds < 1) errors.Add("Rag:RerankerTimeoutSeconds debe ser al menos 1.");
        if (!string.IsNullOrWhiteSpace(options.RerankerUrl) && !OptionsValidation.IsHttpUrl(options.RerankerUrl))
            errors.Add("Rag:RerankerUrl debe estar vacío o ser una URL http(s).");
        var store = options.VectorStore ?? "";
        if (!(store.Equals("sql", StringComparison.OrdinalIgnoreCase) || store.Equals("pinecone", StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Rag:VectorStore debe ser «sql» o «pinecone» (es «{options.VectorStore}»).");
        else if (store.Equals("pinecone", StringComparison.OrdinalIgnoreCase) && !pinecone.CurrentValue.Enabled)
            errors.Add("Rag:VectorStore=pinecone exige Pinecone:Enabled=true.");
        return OptionsValidation.Result(errors);
    }
}

public sealed class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        var errors = new List<string>();
        if (!options.Enabled) return ValidateOptionsResult.Success;
        if (string.IsNullOrWhiteSpace(options.Model)) errors.Add("Embeddings:Model es obligatorio con Embeddings:Enabled=true.");
        if (options.Dimensions is < 1 or > 8192) errors.Add("Embeddings:Dimensions debe estar entre 1 y 8192.");
        if (options.BatchSize is < 1 or > 1000) errors.Add("Embeddings:BatchSize debe estar entre 1 y 1000.");
        if (options.MaxChars < 1) errors.Add("Embeddings:MaxChars debe ser al menos 1.");
        if (options.TimeoutSeconds < 1) errors.Add("Embeddings:TimeoutSeconds debe ser al menos 1.");
        return OptionsValidation.Result(errors);
    }
}

public sealed class PineconeOptionsValidator(IOptionsMonitor<EmbeddingOptions> embeddings) : IValidateOptions<PineconeOptions>
{
    public ValidateOptionsResult Validate(string? name, PineconeOptions options)
    {
        var errors = new List<string>();
        if (!options.Enabled) return ValidateOptionsResult.Success;
        if (string.IsNullOrWhiteSpace(options.IndexName)) errors.Add("Pinecone:IndexName es obligatorio con Pinecone:Enabled=true.");
        if (options.TopK is < 1 or > 1000) errors.Add("Pinecone:TopK debe estar entre 1 y 1000.");
        if (options.MinScore is < -1 or > 1) errors.Add("Pinecone:MinScore debe estar entre -1 y 1.");
        if (options.TimeoutSeconds < 1) errors.Add("Pinecone:TimeoutSeconds debe ser al menos 1.");
        // Antes, un desajuste solo se notaba al consultar: Pinecone rechazaba los vectores o no devolvía nada.
        var embedding = embeddings.CurrentValue;
        if (embedding.Enabled && options.Dimension != embedding.Dimensions)
            errors.Add($"Pinecone:Dimension ({options.Dimension}) debe coincidir con Embeddings:Dimensions ({embedding.Dimensions}).");
        return OptionsValidation.Result(errors);
    }
}

public sealed class ResilienceOptionsValidator : IValidateOptions<ResilienceOptions>
{
    public ValidateOptionsResult Validate(string? name, ResilienceOptions options)
    {
        var errors = new List<string>();
        if (options.MaxAttempts is < 1 or > 10) errors.Add("Resilience:MaxAttempts debe estar entre 1 y 10.");
        if (options.BaseDelayMs < 0) errors.Add("Resilience:BaseDelayMs no puede ser negativo.");
        if (options.MaxDelayMs < options.BaseDelayMs) errors.Add("Resilience:MaxDelayMs debe ser mayor o igual que Resilience:BaseDelayMs.");
        if (options.FailureThreshold < 1) errors.Add("Resilience:FailureThreshold debe ser al menos 1.");
        if (options.BreakSeconds < 1) errors.Add("Resilience:BreakSeconds debe ser al menos 1.");
        if (options.AttemptTimeoutSeconds < 0) errors.Add("Resilience:AttemptTimeoutSeconds no puede ser negativo (0 lo desactiva).");
        return OptionsValidation.Result(errors);
    }
}

public sealed class AuthOptionsValidator : IValidateOptions<AuthOptions>
{
    public ValidateOptionsResult Validate(string? name, AuthOptions options)
    {
        var errors = new List<string>();
        if (options.SessionHours is < 1 or > 168) errors.Add("Auth:SessionHours debe estar entre 1 y 168.");
        if (options.TotpWindow is < 0 or > 3) errors.Add("Auth:TotpWindow debe estar entre 0 y 3.");
        if (options.MaxFailedAttempts < 1) errors.Add("Auth:MaxFailedAttempts debe ser al menos 1.");
        if (options.LockoutMinutes < 1) errors.Add("Auth:LockoutMinutes debe ser al menos 1.");
        for (var index = 0; index < options.Users.Count; index++)
        {
            var user = options.Users[index];
            if (string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@'))
                errors.Add($"Auth:Users:{index}:Email no es un correo válido.");
            if (!Catalog.Roles.Contains(user.Role))
                errors.Add($"Auth:Users:{index}:Role «{user.Role}» no existe ({string.Join(", ", Catalog.Roles)}).");
        }
        // Dos entradas con el mismo correo: la primera gana en silencio y la segunda nunca se usa.
        foreach (var duplicate in options.Users.Where(user => !string.IsNullOrWhiteSpace(user.Email))
                     .GroupBy(user => user.Email.Trim(), StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            errors.Add($"Auth:Users repite el correo {duplicate.Key}.");
        return OptionsValidation.Result(errors);
    }
}
