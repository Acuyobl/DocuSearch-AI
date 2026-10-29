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

    public async Task RefreshAsync()
    {
        Profiles = await service.ListProfilesAsync();
        Data = await service.LoadAsync();
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
