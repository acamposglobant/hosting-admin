using System.Text.Json;
using HostingAdmin.Core.ErrorHandling;
using HostingAdmin.Core.Logging;

namespace HostingAdmin.Api.Middleware;

/// <summary>
/// Catch-all exception handler. Converts any unhandled exception into the canonical
/// 500 error envelope. The response body never contains exception messages, stack
/// traces, paths, or types — those go to the server log only, scrubbed of secrets.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var traceId = context.TraceIdentifier;

            // Structured log with the exception for server-side diagnosis.
            // Only the exception TYPE and a scrubbed message are logged (D7):
            // the raw exception is deliberately not passed to the logger so its
            // message can never bypass redaction via the formatter.
            logger.LogError(
                "Unhandled exception. traceId={TraceId} path={Path} type={ExceptionType} detail={Detail}",
                traceId,
                context.Request.Path,
                ex.GetType().Name,
                SecretRedactor.RedactJsonString(ex.Message));

            if (context.Response.HasStarted)
            {
                // Cannot write the envelope anymore; the connection will be aborted.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            var envelope = ErrorEnvelopeFactory.InternalError(traceId);
            await context.Response.WriteAsync(JsonSerializer.Serialize(envelope, SerializerOptions));
        }
    }
}
