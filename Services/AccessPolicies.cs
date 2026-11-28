using System.Security.Claims;
using DocuSearch.Data;
using DocuSearch.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DocuSearch.Services;

/// <summary>
/// Políticas de autorización de los endpoints HTTP. Se evalúan contra el usuario <b>activo en la base</b>,
/// no contra los claims de la cookie: un usuario desactivado o con el rol rebajado pierde el acceso
/// aunque su cookie siga siendo válida.
/// </summary>
public static class AccessPolicies
{
    /// <summary>Equivale a <see cref="Permissions.CanAdmin"/>.</summary>
    public const string Admin = "docusearch:admin";
    /// <summary>Equivale a <see cref="Permissions.CanAudit"/>.</summary>
    public const string Auditor = "docusearch:auditor";

    public static IServiceCollection AddDocuSearchAuthorization(this IServiceCollection services)
    {
        services.AddScoped<IAuthorizationHandler, ActiveUserHandler>();
        services.AddScoped<HydrateSessionFilter>();
        return services.AddAuthorizationBuilder()
            .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser()
                .AddRequirements(new ActiveUserRequirement(Admin, Permissions.CanAdmin)))
            .AddPolicy(Auditor, policy => policy.RequireAuthenticatedUser()
                .AddRequirements(new ActiveUserRequirement(Auditor, Permissions.CanAudit)))
            .Services;
    }

    /// <summary>Ruta autenticada que, además, hidrata <see cref="LocalSession"/> desde la cookie.</summary>
    public static TBuilder RequireSession<TBuilder>(this TBuilder builder, string? policy = null)
        where TBuilder : IEndpointConventionBuilder
    {
        if (policy is null) builder.RequireAuthorization();
        else builder.RequireAuthorization(policy);
        return builder.AddEndpointFilter<TBuilder, HydrateSessionFilter>();
    }
}

/// <summary>El usuario de la cookie existe, está activo y cumple <see cref="Allowed"/>.</summary>
public sealed record ActiveUserRequirement(string Name, Func<AppUser, bool> Allowed) : IAuthorizationRequirement;

public sealed class ActiveUserHandler(IDbContextFactory<AppDbContext> factory) : AuthorizationHandler<ActiveUserRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveUserRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return;
        await using var db = await factory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == userId && item.Status == "Activo");
        if (user is not null && requirement.Allowed(user)) context.Succeed(requirement);
    }
}

/// <summary>
/// Copia la identidad de la cookie a la <see cref="LocalSession"/> del ámbito de la petición, que es la
/// que usan los servicios. Sustituye al <c>session.SignIn(context.User)</c> repetido en cada endpoint.
/// </summary>
public sealed class HydrateSessionFilter(LocalSession session) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true) session.SignIn(context.HttpContext.User);
        return next(context);
    }
}

/// <summary>Respuestas de autenticación para la API: estado HTTP con JSON en lugar de redirección.</summary>
public static class ApiResponses
{
    /// <summary>Rutas consumidas por programas, no por el navegador.</summary>
    public static bool IsApi(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/assistant");

    /// <summary>
    /// Escribe el estado con un cuerpo JSON. El cuerpo evita además que UseStatusCodePagesWithReExecute
    /// reejecute la petición contra la página «no encontrada» (un POST acababa en 400 por antiforgery).
    /// </summary>
    public static Task WriteAsync(HttpResponse response, int status)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        return response.WriteAsync(status == StatusCodes.Status401Unauthorized
            ? """{"error":"unauthorized"}"""
            : """{"error":"forbidden"}""");
    }

    public static Task RedirectOrStatus(Microsoft.AspNetCore.Authentication.RedirectContext<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions> context, int status)
    {
        if (IsApi(context.Request)) return WriteAsync(context.Response, status);
        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
