namespace DocuSearch.Services.Mcp;

/// <summary>
/// Configuración del endpoint MCP HTTP. El token vive en configuración/entorno y nunca
/// en el repositorio. Para multiusuario real se sustituirá por tokens hasheados en SQL.
/// </summary>
public sealed class McpHttpOptions
{
    public bool Enabled { get; set; }
    public string User { get; set; } = "";
    public string Token { get; set; } = "";
}


public sealed record McpTokenCreateRequest(string Name, int? ValidDays = 90, string? UserId = null);
