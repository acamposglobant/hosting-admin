using System.Text.Json.Serialization;

namespace HostingAdmin.Core.ErrorHandling;

/// <summary>A single field-level validation problem. Messages are operator-facing; never internal details.</summary>
public sealed record ErrorDetail(string Field, string Message);

/// <summary>
/// The body of the error envelope. Contains only the code, a generic operator-facing
/// message, optional field details, and the correlation trace id.
/// No stack traces, paths, versions, or secrets (plan §4).
/// </summary>
public sealed record ErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] IReadOnlyList<ErrorDetail>? Details,
    [property: JsonPropertyName("traceId")] string TraceId);

/// <summary>Envelope wrapper: { "error": { ... } } returned for all non-2xx responses.</summary>
public sealed record ErrorEnvelope(
    [property: JsonPropertyName("error")] ErrorBody Error);

public static class ErrorEnvelopeFactory
{
    public const string GenericInternalErrorMessage = "An unexpected error occurred.";

    public static ErrorEnvelope InternalError(string traceId) => new(
        new ErrorBody(ErrorCodes.InternalError, GenericInternalErrorMessage, null, traceId));

    public static ErrorEnvelope NotFound(string traceId) => new(
        new ErrorBody(ErrorCodes.NotFound, "The requested resource was not found.", null, traceId));

    public static ErrorEnvelope ValidationFailed(IReadOnlyList<ErrorDetail> details, string traceId) => new(
        new ErrorBody(ErrorCodes.ValidationFailed, "One or more parameters are invalid.", details, traceId));

    public static ErrorEnvelope Unauthorized(string traceId) => new(
        new ErrorBody(ErrorCodes.Unauthorized, "Authentication is required.", null, traceId));

    public static ErrorEnvelope Forbidden(string traceId) => new(
        new ErrorBody(ErrorCodes.Forbidden, "Access is denied.", null, traceId));

    public static ErrorEnvelope RateLimited(string traceId) => new(
        new ErrorBody(ErrorCodes.RateLimited, "Too many requests. Try again later.", null, traceId));

    public static ErrorEnvelope CapabilityUnavailable(string traceId) => new(
        new ErrorBody(ErrorCodes.CapabilityUnavailable, "The requested capability is not available on this host.", null, traceId));
}
