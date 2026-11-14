namespace DocuSearch.Services;

/// <summary>Pasos visibles del pipeline de ingesta, en orden de ejecución.</summary>
public enum PipelineStage
{
    Reading,
    Validating,
    Extracting,
    Summarizing,
    Persisting,
    Indexing,
    Completed
}

/// <summary>Actualización de progreso sin contenido del documento.</summary>
public sealed record PipelineProgress(PipelineStage Stage, string Detail, int? Current = null, int? Total = null);
