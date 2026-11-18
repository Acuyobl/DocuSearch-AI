using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace DocuSearch.Services;

/// <summary>Fuente guardada sin el objeto del expediente: al restaurarla se vuelve a validar con RBAC.</summary>
public sealed record MemorySource(string DocumentId, int PageNumber, string Snippet, int Score);

public sealed record MemoryTurn(bool IsUser, string Text, string Provider, bool Numbered, DateTimeOffset At,
    IReadOnlyList<MemorySource> Sources);

/// <summary>Estado del buscador que se recupera al volver a la página.</summary>
public sealed record SearchMemory(string Query, bool Exact, int Minimum, string Department, string Category,
    string Level, string Year, IReadOnlyList<string> History);

/// <summary>Conversación del asistente en una sesión de inicio de sesión.</summary>
public sealed record ConversationSnapshot(IReadOnlyList<MemoryTurn> Turns, string? FocusDocumentCode,
    DateTimeOffset? ExpiresUtc);

/// <summary>
/// Memoria de trabajo del asistente y del buscador, en la memoria del servidor (nunca en disco
/// ni en el navegador). Va ligada al usuario y a su inicio de sesión concreto, y se borra:
/// al cerrar la sesión, 24 h después de empezar, o cuando el usuario la borra. Un reinicio del
/// servidor también la vacía.
/// </summary>
public sealed class AssistantMemory(IMemoryCache cache, TimeProvider? time = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    public const int MaxTurns = 60;
    public const int MaxTurnChars = 12000;
    public const int MaxSearchHistory = 5;

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private sealed class Entry(DateTimeOffset expiresUtc)
    {
        public readonly Lock Gate = new();
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public List<MemoryTurn> Turns { get; } = [];
        public string? FocusDocumentCode { get; set; }
        public SearchMemory? Search { get; set; }
    }

    private static string ChatKey(LocalSession session) => $"memory:chat:{Identity(session)}";
    private static string SearchKey(LocalSession session) => $"memory:search:{Identity(session)}";
    private static string Identity(LocalSession session)
    {
        if (!session.IsAuthenticated)
            throw new UnauthorizedAccessException("No hay una sesión autenticada.");
        return Identity(session.UserId!, session.SessionId);
    }
    private static string Identity(string userId, string? sessionId) => $"{userId}:{sessionId ?? "sin-sesion"}";

    private Entry? Get(string key)
    {
        if (!cache.TryGetValue(key, out Entry? entry) || entry is null) return null;
        if (clock.GetUtcNow() < entry.ExpiresUtc) return entry;
        cache.Remove(key);
        return null;
    }

    private Entry GetOrCreate(string key)
    {
        if (Get(key) is { } existing) return existing;
        var created = new Entry(clock.GetUtcNow().Add(Lifetime));
        cache.Set(key, created, new MemoryCacheEntryOptions { AbsoluteExpiration = created.ExpiresUtc, Size = 1 });
        return created;
    }

    public ConversationSnapshot GetConversation(LocalSession session)
    {
        var entry = Get(ChatKey(session));
        if (entry is null) return new([], null, null);
        lock (entry.Gate) return new(entry.Turns.ToList(), entry.FocusDocumentCode, entry.ExpiresUtc);
    }

    public DateTimeOffset AddTurn(LocalSession session, MemoryTurn turn)
    {
        var entry = GetOrCreate(ChatKey(session));
        var stored = turn.Text.Length > MaxTurnChars ? turn with { Text = turn.Text[..MaxTurnChars] + "…" } : turn;
        lock (entry.Gate)
        {
            entry.Turns.Add(stored);
            if (entry.Turns.Count > MaxTurns) entry.Turns.RemoveRange(0, entry.Turns.Count - MaxTurns);
        }
        return entry.ExpiresUtc;
    }

    public void SetFocus(LocalSession session, string? documentCode)
    {
        var entry = GetOrCreate(ChatKey(session));
        lock (entry.Gate) entry.FocusDocumentCode = documentCode;
    }

    /// <summary>«Nueva conversación»: borra los mensajes y el expediente en foco.</summary>
    public void ClearConversation(LocalSession session) => cache.Remove(ChatKey(session));

    public SearchMemory? GetSearch(LocalSession session)
    {
        var entry = Get(SearchKey(session));
        if (entry is null) return null;
        lock (entry.Gate) return entry.Search;
    }

    public void SaveSearch(LocalSession session, SearchMemory search)
    {
        var entry = GetOrCreate(SearchKey(session));
        var history = search.History.Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.Ordinal).Take(MaxSearchHistory).ToList();
        lock (entry.Gate) entry.Search = search with { History = history };
    }

    public void ClearSearch(LocalSession session) => cache.Remove(SearchKey(session));

    /// <summary>Cierre de sesión: borra todo lo asociado a ese inicio de sesión.</summary>
    public void ClearSession(string userId, string? sessionId)
    {
        cache.Remove($"memory:chat:{Identity(userId, sessionId)}");
        cache.Remove($"memory:search:{Identity(userId, sessionId)}");
    }
}

/// <param name="RetrievalQuery">Texto con el que se recupera (puede incluir la pregunta anterior).</param>
/// <param name="GenerationQuestion">Pregunta enviada al modelo, con el contexto de la conversación.</param>
/// <param name="FocusDocumentCode">Expediente al que parece referirse la pregunta de seguimiento.</param>
public sealed record ConversationHint(string RetrievalQuery, string GenerationQuestion, string? FocusDocumentCode, bool IsFollowUp);

/// <summary>
/// Memoria conversacional determinista: detecta preguntas de seguimiento («¿y su plazo?»,
/// «resume ese documento») y las completa con la pregunta anterior y el expediente en foco.
/// No llama a ningún modelo ni guarda nada.
/// </summary>
public static partial class ConversationContext
{
    /// <summary>Una pregunta de hasta estas palabras casi siempre depende de la anterior («¿y el plazo?»).</summary>
    public const int ShortQuestionWords = 3;

    // Con tildes: «está» (verbo) no debe confundirse con «esta» (demostrativo).
    private static readonly HashSet<string> Anaphora = new(StringComparer.Ordinal)
    {
        "ese", "esa", "esos", "esas", "este", "esta", "estos", "estas", "eso", "esto", "aquel", "aquella",
        "dicho", "dicha", "dichos", "dichas", "anterior", "mismo", "misma", "ahí", "allí", "también", "además"
    };

    [GeneratedRegex(@"\b(?=[A-Z0-9-]*\d)[A-Z]{2,5}-[A-Z0-9]+(?:-[A-Z0-9]+){0,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentCode();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Word();

    public static bool IsFollowUp(string question)
    {
        // Si nombra un expediente, la pregunta es autónoma.
        if (DocumentCode().IsMatch(question)) return false;
        var words = Word().Matches(question.ToLowerInvariant()).Select(match => match.Value).ToList();
        if (words.Count == 0) return false;
        return words[0] is "y" or "e" or "pero" || words.Count <= ShortQuestionWords || words.Any(Anaphora.Contains);
    }

    public static ConversationHint Build(string question, IReadOnlyList<MemoryTurn> history, string? focusDocumentCode)
    {
        var previous = history.LastOrDefault(turn => turn.IsUser)?.Text;
        if (previous is null || !IsFollowUp(question))
            return new(question, question, null, false);
        var retrieval = TextAnalysis.Truncate($"{previous} {question}", 1900);
        var context = $"Contexto de la conversación: la pregunta anterior fue «{TextAnalysis.Truncate(previous, 400)}»" +
            (focusDocumentCode is null ? "" : $" y trataba sobre el expediente {focusDocumentCode}") +
            ". Responde a la pregunta actual teniendo en cuenta ese contexto.";
        var generation = TextAnalysis.Truncate($"{question}\n\n{context}", 2000);
        return new(retrieval, generation, focusDocumentCode, true);
    }
}
