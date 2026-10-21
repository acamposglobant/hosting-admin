namespace HostingAdmin.Core.ErrorHandling;

/// <summary>
/// Canonical error codes used in the API error envelope (plan §4).
/// Codes map to HTTP status codes at the transport layer; they never leak
/// internal details such as stack traces, paths, or exception types.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string RateLimited = "rate_limited";
    public const string InternalError = "internal_error";
    public const string CapabilityUnavailable = "capability_unavailable";
}
