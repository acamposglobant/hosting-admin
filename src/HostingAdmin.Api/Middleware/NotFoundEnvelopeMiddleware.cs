using System.Text.Json;
using HostingAdmin.Core.ErrorHandling;

namespace HostingAdmin.Api.Middleware;

/// <summary>
/// Converts unmatched routes (404 with an empty body) into the canonical error
/// envelope so that every non-2xx response has the same shape (plan §4).
/// Responses that already have a body (e.g. envelopes from other handlers) are
/// left untouched.
/// </summary>
public sealed class NotFoundEnvelopeMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Response.StatusCode == StatusCodes.Status404NotFound &&
            context.Response.ContentLength is null or 0 &&
            !context.Response.HasStarted)
        {
            context.Response.ContentType = "application/json";
            var envelope = ErrorEnvelopeFactory.NotFound(context.TraceIdentifier);
            await context.Response.WriteAsync(JsonSerializer.Serialize(envelope, SerializerOptions));
        }
    }
}
