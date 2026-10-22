namespace HostingAdmin.Core.Proxy;

/// <summary>
/// Trusted-proxy configuration (plan D2/D3, task 9). The API binds to loopback
/// in production (primary control, D2); the shared-secret attestation header is
/// defense in depth, never a substitute. Forwarded headers are honored only
/// from explicitly configured proxy addresses, validated against the actual
/// connection remote address. The secret is credential material: never logged,
/// never returned (D7).
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>
    /// When true, every API request (except the liveness probe) must originate
    /// from a trusted proxy address AND carry the attestation header with the
    /// exact shared secret. Production-like configuration sets this to true.
    /// </summary>
    public bool RequireAttestation { get; set; }

    /// <summary>Header the trusted proxy sets to attest the request. Default "X-Proxy-Attestation".</summary>
    public string AttestationHeaderName { get; set; } = "X-Proxy-Attestation";

    /// <summary>
    /// Shared secret between the proxy and the API. Configured out-of-band
    /// (environment/user-secrets) in production; empty by default. Never logged (D7).
    /// </summary>
    public string AttestationSecret { get; set; } = string.Empty;

    /// <summary>
    /// Exact IP addresses from which forwarded headers are honored and attestation
    /// is accepted. Defaults to loopback (proxy on the same host, D2).
    /// </summary>
    public string[] TrustedProxyAddresses { get; set; } = ["127.0.0.1", "::1"];
}
