using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DocuSearch.Models;
using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace DocuSearch.Services;

public sealed class WorkspaceState(DocumentService service, ILogger<WorkspaceState> logger)
{
    public AppSnapshot? Data { get; private set; }
    public List<AppUser> Profiles { get; private set; } = [];
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public string? Notice { get; private set; }
    public event Action? Changed;

    private AppSnapshot? vectorsLoadedFor;

    public async Task RefreshAsync()
    {
        Profiles = await service.ListProfilesAsync();
        // Sin vectores por página: cada pestaña retiene esta instantánea y solo la búsqueda de
        // respaldo los usa (ver EnsurePageVectorsAsync).
        Data = await service.LoadAsync(SnapshotParts.Directory | SnapshotParts.Audit);
    }

    /// <summary>
    /// Carga los vectores por página de la instantánea actual la primera vez que la búsqueda
    /// híbrida de respaldo los necesita. Una recarga de <see cref="Data"/> los descarta.
    /// </summary>
    public async Task EnsurePageVectorsAsync()
    {
        if (Data is not { } snapshot || ReferenceEquals(vectorsLoadedFor, snapshot)) return;
        await service.AttachPageVectorsAsync(snapshot.Documents);
        vectorsLoadedFor = snapshot;
    }
    public async Task RunAsync(Func<Task> operation, string? success = null)
    {
        Busy = true;
        Error = null;
        Notice = null;
        Changed?.Invoke();
        try
        {
            await operation();
            Notice = success;
        }
        catch (ApprovalPendingException pending)
        {
            // Doble control: no es un error, la acción quedó en cola sin aplicarse.
            Notice = pending.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "La operación documental no pudo completarse.");
            if (ex is UnauthorizedAccessException) Data = null;
            Error = ex is UnauthorizedAccessException
                ? "Tu sesión ya no es válida o el usuario fue desactivado. Vuelve a iniciar sesión."
                : IsDatabaseError(ex)
                    ? "No se pudo completar la operación en SQL Server. Comprueba la instancia, los permisos de Windows y la inicialización. El detalle está en la consola del servidor."
                    : ex.Message;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    private static bool IsDatabaseError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is DbException or DbUpdateException) return true;
        return false;
    }
}
