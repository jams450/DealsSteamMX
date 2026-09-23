using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Deals.API;

public sealed class ExceptionHandler(ILogger<ExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled exception while processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

        var (status, title, detail) = exception switch
        {
            ArgumentException => (StatusCodes.Status400BadRequest, "Solicitud no válida", "Revise los datos enviados."),
            Deals.BusinessLogic.Exceptions.GameNotFoundException => (StatusCodes.Status404NotFound, "No encontrado", "El juego indicado no existe."),
            // Provider answered, no such row: nothing was written and a retry with another id may work.
            Deals.BusinessLogic.Exceptions.GameTitleSourceNotFoundException => (StatusCodes.Status404NotFound, "No encontrado", "El juego indicado no existe en el proveedor de títulos."),
            // Provider unreachable or unconfigured: nothing was written, the caller can retry later.
            Deals.BusinessLogic.Exceptions.GameTitleSourceUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Servicio no disponible", "El proveedor de títulos no está disponible; intente de nuevo."),
            // Cover provider answered, no usable artwork: nothing was written, another id may work.
            Deals.BusinessLogic.Exceptions.GameCoverSourceNotFoundException => (StatusCodes.Status404NotFound, "No encontrado", "Ese juego no existe en el proveedor de portadas o no tiene portada."),
            // Cover provider unreachable or unconfigured: nothing was written, the caller can retry later.
            Deals.BusinessLogic.Exceptions.GameCoverSourceUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Servicio no disponible", "El proveedor de portadas no está disponible; intente de nuevo."),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "No autorizado", "No fue posible autenticar la solicitud."),
            // Kestrel raises this when the endpoint/body size limit is exceeded; keep its status (413).
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Solicitud no válida", "Revise los datos enviados."),
            _ => (StatusCodes.Status500InternalServerError, "Error interno del servidor", "Ocurrió un error al procesar la solicitud.")
        };

        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        }, cancellationToken);

        return true;
    }
}
