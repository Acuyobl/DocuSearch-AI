using Microsoft.Extensions.Configuration;

namespace DocuSearch.Services;

/// <summary>Resultado de la validación de configuración para exponer la aplicación.</summary>
public sealed record ProductionReport(IReadOnlyList<string> Fatal, IReadOnlyList<string> Warnings);

/// <summary>
/// Comprueba que una configuración que deja de ser local (Demo:LocalOnly=false) no arrastre
/// los atajos de la demo: sin TLS, alta de dispositivos, reset, seed o secretos inline.
/// No bloquea la ejecución local de la demo.
/// </summary>
public static class ProductionGuard
{
    public static ProductionReport Validate(IConfiguration configuration)
    {
        var fatal = new List<string>();
        var warnings = new List<string>();

        // Mientras la aplicación sea local, los atajos de demo son deliberados.
        if (configuration.GetValue("Demo:LocalOnly", true)) return new(fatal, warnings);

        // Salida explícita para una demo embebida en contenedor sin TLS.
        if (configuration.GetValue("Demo:AllowInsecureExposure", false))
        {
            warnings.Add("Demo:AllowInsecureExposure=true: la aplicación se expone sin los controles de producción. Úsalo solo en una demo controlada.");
            return new(fatal, warnings);
        }

        if (!configuration.GetValue("Security:Https:Enabled", false))
            fatal.Add("Security:Https:Enabled debe ser true antes de exponer la aplicación.");
        if (configuration.GetValue("Auth:EnrollmentEnabled", false))
            fatal.Add("Auth:EnrollmentEnabled debe ser false: /enroll muestra secretos TOTP.");
        if (configuration.GetValue("Demo:AllowReset", true))
            fatal.Add("Demo:AllowReset debe ser false: elimina datos reales.");
        if (configuration.GetValue("Demo:SeedOnInitialize", true))
            fatal.Add("Demo:SeedOnInitialize debe ser false: inserta expedientes de ejemplo.");

        foreach (var user in configuration.GetSection("Auth:Users").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(user["TotpSecret"]))
                fatal.Add($"Auth:Users:{user.Key}:TotpSecret no debe estar en appsettings; usa user-secrets o un IdP.");
        }

        var connection = configuration.GetConnectionString("Documents") ?? "";
        if (connection.Contains("TrustServerCertificate=True", StringComparison.OrdinalIgnoreCase))
            fatal.Add("La cadena de conexión no debe usar TrustServerCertificate=True en producción.");

        var hosts = configuration["AllowedHosts"] ?? "";
        var hostList = hosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hostList.Length == 0 || hostList.Any(host => host == "*"
                || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || host == "127.0.0.1" || host == "[::1]"))
            fatal.Add("AllowedHosts debe declarar el dominio real, no localhost ni comodines.");

        if (!configuration.GetValue("Security:ForwardedHeaders:Enabled", false))
            warnings.Add("Si hay un proxy inverso delante, activa Security:ForwardedHeaders:Enabled y declara KnownProxies.");
        if (string.IsNullOrWhiteSpace(configuration["Security:ContentSecurityPolicy"]))
            warnings.Add("Security:ContentSecurityPolicy está vacía: define una CSP antes de exponer la aplicación.");
        if (configuration.GetValue("Security:LoginRateLimitPerMinute", 10) <= 0)
            warnings.Add("Security:LoginRateLimitPerMinute debería ser mayor que cero.");

        return new(fatal, warnings);
    }
}
