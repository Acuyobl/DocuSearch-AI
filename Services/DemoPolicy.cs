namespace DocuSearch.Services;

/// <summary>
/// Interruptores exclusivos de la demo. En producción deben quedar en false:
/// <see cref="ProductionGuard"/> los valida al arrancar cuando la aplicación se expone.
/// </summary>
public static class DemoPolicy
{
    /// <summary>Permite restablecer los datos de ejemplo desde Administración.</summary>
    public static bool AllowReset { get; set; } = true;

    /// <summary>Siembra los expedientes y usuarios de ejemplo al inicializar una base vacía.</summary>
    public static bool SeedOnInitialize { get; set; } = true;
}
