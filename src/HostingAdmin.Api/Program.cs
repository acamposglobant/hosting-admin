using System.Text.Json;
using HostingAdmin.Api.Middleware;
using HostingAdmin.Api.Security;
using HostingAdmin.Api.Sessions;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Auth;
using HostingAdmin.Core.Bootstrap;
using HostingAdmin.Core.Metrics;
using HostingAdmin.Core.Proxy;
using HostingAdmin.Core.Security;
using HostingAdmin.Core.Services;
using HostingAdmin.Core.Sessions;
using HostingAdmin.Core.SystemInfo;
using HostingAdmin.Core.Agent;
using HostingAdmin.Infrastructure.Agent;
using HostingAdmin.Infrastructure.Persistence;
using HostingAdmin.Infrastructure.Linux;
using SessionOptions = HostingAdmin.Core.Sessions.SessionOptions;

var builder = WebApplication.CreateBuilder(args);

// D2 (primary control): in production the API binds to loopback — the reverse
// proxy runs on the same host. Configured via Network:Urls in
// appsettings.Production.json; Development keeps launchSettings control.
var networkUrls = builder.Configuration["Network:Urls"];
if (!string.IsNullOrWhiteSpace(networkUrls))
{
    builder.WebHost.UseUrls(networkUrls);
}

// Structured logging: JSON console formatter so log records are machine-parseable
// (plan §4). Secret redaction is applied in the exception middleware and must also
// be applied by any code that logs user-influenced state (D7).
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

// Options with startup validation (engineering standards).
builder.Services
    .AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.Path), "Database:Path must be configured.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AuditOptions>()
    .Bind(builder.Configuration.GetSection(AuditOptions.SectionName))
    .Validate(o => o.RetentionDays > 0, "Audit:RetentionDays must be positive.")
    .Validate(o => o.PruneIntervalHours > 0, "Audit:PruneIntervalHours must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton<DatabaseOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value);
builder.Services
    .AddOptions<SessionOptions>()
    .Bind(builder.Configuration.GetSection(SessionOptions.SectionName))
    .Validate(o => o.AbsoluteLifetime > TimeSpan.Zero, "Sessions:AbsoluteLifetime must be positive.")
    .Validate(o => o.IdleTimeout is null || o.IdleTimeout > TimeSpan.Zero, "Sessions:IdleTimeout must be positive when set.")
    .Validate(o => o.IdleTimeout is null || o.IdleTimeout < o.AbsoluteLifetime, "Sessions:IdleTimeout must be shorter than AbsoluteLifetime.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.CookieName), "Sessions:CookieName must be configured.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .Validate(o => o.MaxFailedLoginAttempts > 0, "Auth:MaxFailedLoginAttempts must be positive.")
    .Validate(o => o.LockoutWindow > TimeSpan.Zero, "Auth:LockoutWindow must be positive.")
    .ValidateOnStart();

builder.Services
    .AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .Validate(o => o.AllowedOrigins.Length > 0 && o.AllowedOrigins.All(origin => !string.IsNullOrWhiteSpace(origin)),
        "Security:AllowedOrigins must list at least one explicit origin (fails closed otherwise).")
    .Validate(o => !string.IsNullOrWhiteSpace(o.CsrfCookieName), "Security:CsrfCookieName must be configured.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.CsrfHeaderName), "Security:CsrfHeaderName must be configured.")
    .ValidateOnStart();

builder.Services
    .AddOptions<BootstrapOptions>()
    .Bind(builder.Configuration.GetSection(BootstrapOptions.SectionName))
    .Validate(o => o.WindowDuration > TimeSpan.Zero, "Bootstrap:WindowDuration must be positive.")
    .Validate(o => o.WindowDuration <= TimeSpan.FromHours(1), "Bootstrap:WindowDuration must be at most one hour.")
    .Validate(o => o.MinPasswordLength > 0, "Bootstrap:MinPasswordLength must be positive.")
    .Validate(o => o.MaxPasswordLength >= o.MinPasswordLength, "Bootstrap:MaxPasswordLength must be >= MinPasswordLength.")
    .ValidateOnStart();

builder.Services
    .AddOptions<ProxyOptions>()
    .Bind(builder.Configuration.GetSection(ProxyOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.AttestationHeaderName), "Proxy:AttestationHeaderName must be configured.")
    .Validate(o => !o.RequireAttestation || !string.IsNullOrEmpty(o.AttestationSecret),
        "Proxy:AttestationSecret must be configured when Proxy:RequireAttestation is enabled.")
    .Validate(o => o.TrustedProxyAddresses.Length > 0 &&
               o.TrustedProxyAddresses.All(address => System.Net.IPAddress.TryParse(address, out _)),
        "Proxy:TrustedProxyAddresses must list valid IP addresses.")
    .ValidateOnStart();

builder.Services.AddSingleton<AuditOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value);
builder.Services.AddSingleton<SessionOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionOptions>>().Value);
builder.Services.AddSingleton<AuthOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value);
builder.Services.AddSingleton<SecurityOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value);
builder.Services.AddSingleton<BootstrapOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BootstrapOptions>>().Value);
builder.Services.AddSingleton<ProxyOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyOptions>>().Value);
builder.Services
    .AddOptions<MetricsOptions>()
    .Bind(builder.Configuration.GetSection(MetricsOptions.SectionName))
    .Validate(o => o.CacheTtl > TimeSpan.Zero, "Metrics:CacheTtl must be positive.")
    .Validate(o => o.CollectionTimeout > TimeSpan.Zero, "Metrics:CollectionTimeout must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<MetricsOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MetricsOptions>>().Value);
// Runtime detection (D11): procfs on Linux, structured "unavailable" elsewhere.
if (OperatingSystem.IsLinux() && File.Exists("/proc/stat"))
{
    builder.Services.AddSingleton<IMetricsProvider, ProcFsMetricsProvider>();
}
else
{
    builder.Services.AddSingleton<IMetricsProvider, NullMetricsProvider>();
}
builder.Services.AddSingleton<MetricsService>();
// Runtime detection (D11): os-release/procfs + statvfs on Linux, structured
// "unavailable" elsewhere (task 11).
if (OperatingSystem.IsLinux() && File.Exists("/etc/os-release"))
{
    builder.Services.AddSingleton<ISystemInfoProvider, LinuxSystemInfoProvider>();
}
else
{
    builder.Services.AddSingleton<ISystemInfoProvider, NullSystemInfoProvider>();
}
if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts"))
{
    builder.Services.AddSingleton<IDiskProvider, LinuxDiskProvider>();
}
else
{
    builder.Services.AddSingleton<IDiskProvider, NullDiskProvider>();
}
builder.Services.AddSingleton<SqliteConnectionFactory>();
// Service status options (task 12, D12): the unit allowlist is validated at
// startup — invalid configuration fails startup instead of degrading silently.
builder.Services
    .AddOptions<ServiceStatusOptions>()
    .Bind(builder.Configuration.GetSection(ServiceStatusOptions.SectionName))
    .Validate(o => o.UnitNames.Count > 0, "Services:UnitNames must contain at least one unit.")
    .Validate(o => o.UnitNames.All(ServiceStatusOptions.IsValidUnitName), "Services:UnitNames contains an invalid systemd unit name.")
    .Validate(o => Path.IsPathRooted(o.SystemctlPath), "Services:SystemctlPath must be an absolute path.")
    .Validate(o => o.Timeout > TimeSpan.Zero, "Services:Timeout must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<ServiceStatusOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServiceStatusOptions>>().Value);
builder.Services.AddSingleton<ISystemCtlLauncher>(sp =>
{
    var options = sp.GetRequiredService<ServiceStatusOptions>();
    return new SystemCtlLauncher(options.SystemctlPath);
});
// Runtime detection (D11): systemctl on Linux, structured "unavailable" elsewhere.
if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IServiceStatusProvider, SystemdServiceStatusProvider>();
}
else
{
    builder.Services.AddSingleton<IServiceStatusProvider, NullServiceStatusProvider>();
}
// Agent channel (increment 2, task 3, D22/D24): fail-fast options validation;
// the shared secret is read once at startup and held in memory only (D25).
// On non-Linux hosts the null channel reports structured "unavailable" (D17).
builder.Services
    .AddOptions<AgentChannelOptions>()
    .Bind(builder.Configuration.GetSection(AgentChannelOptions.SectionName))
    .Validate(o => Path.IsPathRooted(o.SocketPath), "AgentChannel:SocketPath must be an absolute path.")
    .Validate(o => Path.IsPathRooted(o.SecretFile), "AgentChannel:SecretFile must be an absolute path.")
    .Validate(o => o.CallTimeout > TimeSpan.Zero, "AgentChannel:CallTimeout must be positive.")
    .Validate(o => o.ProbeTimeout > TimeSpan.Zero, "AgentChannel:ProbeTimeout must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<AgentChannelOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentChannelOptions>>().Value);
if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IAgentChannel>(sp =>
    {
        var channelOptions = sp.GetRequiredService<AgentChannelOptions>();
        var secret = AgentSharedSecret.Load(channelOptions.SecretFile);
        return new UnixDomainSocketAgentChannel(
            channelOptions, secret, sp.GetRequiredService<ILogger<UnixDomainSocketAgentChannel>>());
    });
}
else
{
    builder.Services.AddSingleton<IAgentChannel, NullAgentChannel>();
}
builder.Services.AddSingleton<IAuditStore, SqliteAuditStore>();
// Selective audit-on-access (plan task 13, §3.7): first data access per session
// is audited; routine polling is not (D8). Failure grouping records state
// transitions into failure and recovery, not every repeated occurrence (T16).
builder.Services.AddSingleton<SelectiveAuditService>();
builder.Services.AddSingleton<FailureAuditGrouper>();
builder.Services.AddSingleton<ISessionStore, SqliteSessionStore>();
builder.Services.AddSingleton<IAdminCredentialStore, SqliteAdminCredentialStore>();
builder.Services.AddSingleton<ILoginThrottleStore, SqliteLoginThrottleStore>();
builder.Services.AddSingleton<IBootstrapStateStore, SqliteBootstrapStateStore>();
// Service-action confirmations (plan task 4, D20): single-use, session-bound,
// short-TTL tokens for destructive mutations; pruned by the maintenance job.
builder.Services.AddSingleton<IServiceActionConfirmationStore, SqliteServiceActionConfirmationStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServiceActionService>();
builder.Services.AddSingleton<LoginService>();
builder.Services.AddSingleton<BootstrapService>();
builder.Services.AddHostedService<AuditRetentionPruneService>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<ProxyTrustMiddleware>();
app.UseMiddleware<NotFoundEnvelopeMiddleware>();
app.UseMiddleware<CsrfOriginMiddleware>();

// Minimal liveness endpoint — no authentication, no internal application details.
// Per plan §4: status only. The version is a static assembly version, not runtime detail.
app.MapGet("/api/health", () => Results.Json(new
{
    status = "healthy",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
    timestamp = DateTime.UtcNow
}));

// Test-only endpoint that forces an unhandled exception, used to verify the global
// error envelope. Double-gated: requires the configuration flag AND a non-production
// environment, and is DISABLED by default; never reachable in production (plan task 2).
if (builder.Configuration.GetValue<bool>("Diagnostics:ForceErrorEnabled") &&
    !app.Environment.IsProduction())
{
    app.MapGet("/api/diagnostics/force-error", (HttpContext context) =>
    {
        throw new InvalidOperationException("Forced test exception");
    });
}

// Real authentication endpoints (plan §3.3, task 6): single admin, PBKDF2
// credential, DB-backed login rate limiting, audit of auth events (T12–T14).
// Failure responses are always generic — no distinction between "wrong
// password" and "no credential configured" (D7). CSRF + Origin validation for
// cookie-authenticated requests is task 7 (D4); login itself is pre-session.
app.MapPost("/api/auth/login", async (
    LoginRequest? request,
    HttpContext context,
    LoginService loginService,
    SessionOptions sessionOptions,
    SecurityOptions securityOptions,
    ISessionStore sessionStore,
    IAuditStore auditStore) =>
{
    var sourceIp = context.Connection.RemoteIpAddress?.ToString();

    if (request is null || string.IsNullOrEmpty(request.Password))
    {
        return Results.Json(
            new { error = new { code = "validation_failed", message = "A password is required." } },
            statusCode: StatusCodes.Status400BadRequest);
    }

    var nowUtc = DateTime.UtcNow;
    var result = await loginService.AuthenticateAsync(request.Password, nowUtc, context.RequestAborted);

    if (result.IsLockedOut)
    {
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = nowUtc,
            Actor = "anonymous",
            Action = "auth.login",
            Outcome = AuditOutcomes.Failure,
            SourceIp = sourceIp,
            Detail = "locked_out",
        });
        return Results.Json(
            new { error = new { code = "rate_limited", message = "Too many failed attempts. Try again later." } },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    if (!result.Succeeded)
    {
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = nowUtc,
            Actor = "anonymous",
            Action = "auth.login_failed",
            Outcome = AuditOutcomes.Failure,
            SourceIp = sourceIp,
            Detail = null,
        });
        return Results.Json(
            new { error = new { code = "unauthorized", message = "Invalid credentials." } },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    // Success: issue a fresh session (rotation on login — a new opaque id every
    // time), cookie carries the raw id; the store keeps only its hash (T11).
    // The CSRF cookie (double-submit token, D4) is issued alongside; the SPA
    // echoes it in the configured header on every cookie-authenticated request.
    var (rawId, record) = new SessionService(sessionOptions).Create("admin", nowUtc);
    await sessionStore.InsertAsync(record, context.RequestAborted);
    SessionCookieWriter.Issue(context, sessionOptions.CookieName, rawId, sessionOptions.AbsoluteLifetime);
    CsrfCookieWriter.Issue(context, securityOptions.CsrfCookieName, CsrfCookieWriter.GenerateToken(),
        sessionOptions.AbsoluteLifetime);

    await auditStore.AppendAsync(new AuditEvent
    {
        TimestampUtc = nowUtc,
        Actor = "admin",
        Action = "auth.login",
        Outcome = AuditOutcomes.Success,
        SourceIp = sourceIp,
        Detail = null,
    });

    return Results.Ok(new { status = "authenticated" });
});

app.MapPost("/api/auth/logout", async (
    HttpContext context,
    SessionOptions sessionOptions,
    SecurityOptions securityOptions,
    ISessionStore sessionStore,
    IAuditStore auditStore) =>
{
    if (!context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var idHash = SessionService.HashId(rawId);
    var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
    if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    await sessionStore.RevokeAsync(idHash, context.RequestAborted);
    SessionCookieWriter.Clear(context, sessionOptions.CookieName);
    CsrfCookieWriter.Clear(context, securityOptions.CsrfCookieName);

    await auditStore.AppendAsync(new AuditEvent
    {
        TimestampUtc = DateTime.UtcNow,
        Actor = record.AdminId,
        Action = "auth.logout",
        Outcome = AuditOutcomes.Success,
        SourceIp = context.Connection.RemoteIpAddress?.ToString(),
        Detail = null,
    });

    return Results.Ok(new { status = "logged_out" });
});

// Metrics endpoint (plan §4, task 10): session + CSRF (enforced by the CSRF
// middleware whenever the session cookie is presented). Served from the bounded
// cache (D10) and NOT audited per poll (D8). On platforms without a metrics
// source the structured "unavailable" shape is returned (D11).
app.MapGet("/api/metrics", async (
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    SelectiveAuditService selectiveAuditService,
    MetricsService metricsService) =>
{
    if (!context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var idHash = SessionService.HashId(rawId);
    var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
    if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // First data access per session is audited; routine polling is not (D8).
    await selectiveAuditService.AuditDataAccessIfNeededAsync(
        idHash, "/api/metrics", context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    var snapshot = await metricsService.GetSnapshotAsync(context.RequestAborted);
    return Results.Json(snapshot);
});

// System info endpoint (plan §4, task 11): session + CSRF. Not audited per
// poll (D8). Structured "unavailable" shape when no source exists (D11).
app.MapGet("/api/system/info", async (
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    SelectiveAuditService selectiveAuditService,
    ISystemInfoProvider systemInfoProvider) =>
{
    if (!context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var idHash = SessionService.HashId(rawId);
    var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
    if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // First data access per session is audited; routine polling is not (D8).
    await selectiveAuditService.AuditDataAccessIfNeededAsync(
        idHash, "/api/system/info", context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    var info = await systemInfoProvider.GetInfoAsync(context.RequestAborted);
    return Results.Json(info);
});

// Disk usage endpoint (plan §4, task 11): real filesystems only; session + CSRF;
// not audited per poll (D8). Structured "unavailable" shape when no source (D11).
app.MapGet("/api/disks", async (
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    SelectiveAuditService selectiveAuditService,
    IDiskProvider diskProvider) =>
{
    if (!context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var idHash = SessionService.HashId(rawId);
    var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
    if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // First data access per session is audited; routine polling is not (D8).
    await selectiveAuditService.AuditDataAccessIfNeededAsync(
        idHash, "/api/disks", context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    var disks = await diskProvider.GetDisksAsync(context.RequestAborted);
    return Results.Json(disks);
});

// Service status endpoint (plan §4, task 12, D12): units come only from the
// server-side allowlist — the endpoint accepts no client parameters at all.
// Session + CSRF; not audited per poll (D8). Structured "unavailable" shape
// when systemd is not available (D11).
app.MapGet("/api/services", async (
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    SelectiveAuditService selectiveAuditService,
    IServiceStatusProvider serviceStatusProvider,
    IAgentChannel agentChannel) =>
{
    if (!context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var idHash = SessionService.HashId(rawId);
    var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
    if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // First data access per session is audited; routine polling is not (D8).
    await selectiveAuditService.AuditDataAccessIfNeededAsync(
        idHash, "/api/services", context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    var snapshot = await serviceStatusProvider.GetStatusAsync(context.RequestAborted);

    // Mutation capability (task 3, D24): a short-timeout authenticated probe
    // of the agent. Status reporting is unaffected by agent health (D24).
    var mutationCapabilityAvailable = agentChannel.IsAvailable &&
        (await agentChannel.ProbeAsync(context.RequestAborted)).Outcome == AgentOperationOutcome.Success;

    return Results.Json(new
    {
        snapshot.CapabilityAvailable,
        snapshot.UnavailableReason,
        snapshot.Services,
        MutationCapabilityAvailable = mutationCapabilityAvailable,
    });
});

// Service mutation endpoints (plan task 4, §4): session + CSRF (CSRF enforced
// globally by CsrfOriginMiddleware). Unit names are accepted only as exact
// allowlist matches (D18); non-allowlisted units get 404 without revealing the
// allowlist, and the agent channel is never called (TA8). Agent failures map
// to capability_unavailable (503) — never agent internals (D24).
async Task<(bool Authenticated, string IdHash)> RequireSessionAsync(
    HttpContext context, SessionOptions sessionOptions, ISessionStore sessionStore)
{
    if (context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        var idHash = SessionService.HashId(rawId);
        var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
        if (record is not null && SessionService.IsValid(record, DateTime.UtcNow))
        {
            return (true, idHash);
        }
    }

    return (false, string.Empty);
}

app.MapPost("/api/services/{unit}/start", async (
    string unit,
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    ServiceActionService serviceActionService) =>
{
    var (authenticated, idHash) = await RequireSessionAsync(context, sessionOptions, sessionStore);
    if (!authenticated)
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await serviceActionService.RequestActionAsync(
        unit, "start", idHash, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    return result.Outcome switch
    {
        ServiceActionRequestOutcome.UnitNotAllowed =>
            Results.Json(new { error = new { code = "not_found", message = "Unknown service." } },
                statusCode: StatusCodes.Status404NotFound),
        ServiceActionRequestOutcome.CapabilityUnavailable =>
            Results.Json(new { error = new { code = "capability_unavailable", message = "Service control is unavailable." } },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        ServiceActionRequestOutcome.Executed when result.Execution!.Outcome == AgentOperationOutcome.Success =>
            Results.Ok(new
            {
                unit,
                action = "start",
                outcome = result.Execution.Outcome.ToString(),
                completedAt = result.Execution.CompletedUtc,
            }),
        ServiceActionRequestOutcome.Executed =>
            Results.Json(new { error = new { code = "capability_unavailable", message = "Service control is unavailable." } },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new { error = new { code = "internal_error", message = "The operation failed." } },
            statusCode: StatusCodes.Status500InternalServerError),
    };
});

app.MapPost("/api/services/{unit}/stop", async (
    string unit,
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    ServiceActionService serviceActionService) =>
{
    var (authenticated, idHash) = await RequireSessionAsync(context, sessionOptions, sessionStore);
    if (!authenticated)
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await serviceActionService.RequestActionAsync(
        unit, "stop", idHash, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    return result.Outcome switch
    {
        ServiceActionRequestOutcome.UnitNotAllowed =>
            Results.Json(new { error = new { code = "not_found", message = "Unknown service." } },
                statusCode: StatusCodes.Status404NotFound),
        ServiceActionRequestOutcome.ConfirmationRequired =>
            Results.Ok(new
            {
                confirmationId = result.Pending!.ConfirmationId,
                unit = result.Pending.Unit,
                action = result.Pending.Action,
                expiresAt = result.Pending.ExpiresUtc,
            }),
        _ => Results.Json(
            new { error = new { code = "internal_error", message = "The operation failed." } },
            statusCode: StatusCodes.Status500InternalServerError),
    };
});

app.MapPost("/api/services/{unit}/restart", async (
    string unit,
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    ServiceActionService serviceActionService) =>
{
    var (authenticated, idHash) = await RequireSessionAsync(context, sessionOptions, sessionStore);
    if (!authenticated)
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await serviceActionService.RequestActionAsync(
        unit, "restart", idHash, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    return result.Outcome switch
    {
        ServiceActionRequestOutcome.UnitNotAllowed =>
            Results.Json(new { error = new { code = "not_found", message = "Unknown service." } },
                statusCode: StatusCodes.Status404NotFound),
        ServiceActionRequestOutcome.ConfirmationRequired =>
            Results.Ok(new
            {
                confirmationId = result.Pending!.ConfirmationId,
                unit = result.Pending.Unit,
                action = result.Pending.Action,
                expiresAt = result.Pending.ExpiresUtc,
            }),
        _ => Results.Json(
            new { error = new { code = "internal_error", message = "The operation failed." } },
            statusCode: StatusCodes.Status500InternalServerError),
    };
});

app.MapPost("/api/service-actions/{id}/confirm", async (
    string id,
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    ServiceActionService serviceActionService) =>
{
    var (authenticated, idHash) = await RequireSessionAsync(context, sessionOptions, sessionStore);
    if (!authenticated)
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await serviceActionService.ConfirmAsync(
        id, idHash, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    return result.Outcome switch
    {
        ServiceActionRequestOutcome.InvalidConfirmation =>
            Results.Json(new { error = new { code = "invalid_confirmation", message = "The confirmation is no longer valid." } },
                statusCode: StatusCodes.Status409Conflict),
        ServiceActionRequestOutcome.CapabilityUnavailable =>
            Results.Json(new { error = new { code = "capability_unavailable", message = "Service control is unavailable." } },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        ServiceActionRequestOutcome.Executed when result.Execution!.Outcome == AgentOperationOutcome.Success =>
            Results.Ok(new
            {
                unit = result.Execution.Unit,
                action = result.Execution.Action,
                outcome = result.Execution.Outcome.ToString(),
                completedAt = result.Execution.CompletedUtc,
            }),
        ServiceActionRequestOutcome.Executed =>
            Results.Json(new { error = new { code = "capability_unavailable", message = "Service control is unavailable." } },
                statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(
            new { error = new { code = "internal_error", message = "The operation failed." } },
            statusCode: StatusCodes.Status500InternalServerError),
    };
});

app.MapPost("/api/service-actions/{id}/cancel", async (
    string id,
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    ServiceActionService serviceActionService) =>
{
    var (authenticated, idHash) = await RequireSessionAsync(context, sessionOptions, sessionStore);
    if (!authenticated)
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var result = await serviceActionService.CancelAsync(
        id, idHash, context.Connection.RemoteIpAddress?.ToString(), context.RequestAborted);

    return result.Outcome switch
    {
        ServiceActionRequestOutcome.InvalidConfirmation =>
            Results.Json(new { error = new { code = "invalid_confirmation", message = "The confirmation is no longer valid." } },
                statusCode: StatusCodes.Status409Conflict),
        ServiceActionRequestOutcome.Cancelled => Results.Ok(new { status = "cancelled" }),
        _ => Results.Json(
            new { error = new { code = "internal_error", message = "The operation failed." } },
            statusCode: StatusCodes.Status500InternalServerError),
    };
});

// Audit query endpoint (plan §4, task 13): paged, newest first; take capped at
// 200 (plan §3.7). Session + CSRF. Audit-log views are themselves audited (D8:
// "audit-log views" are relevant access events) — every view, not just the
// first, since inspecting the audit trail is a security-relevant action.
app.MapGet("/api/audit", async (
    HttpContext context,
    SessionOptions sessionOptions,
    ISessionStore sessionStore,
    IAuditStore auditStore) =>
{
    if (!context.Request.Cookies.TryGetValue(sessionOptions.CookieName, out var rawId))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    var idHash = SessionService.HashId(rawId);
    var record = await sessionStore.GetByIdHashAsync(idHash, context.RequestAborted);
    if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
    {
        return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // Bounded pagination (plan §4): skip >= 0, 1 <= take <= 200.
    var skipParsed = int.TryParse(context.Request.Query["skip"], out var parsedSkip) ? parsedSkip : 0;
    var takeParsed = int.TryParse(context.Request.Query["take"], out var parsedTake) ? parsedTake : 50;
    var validationErrors = new List<(string Field, string Message)>();
    if (skipParsed < 0)
    {
        validationErrors.Add(("skip", "Must be zero or greater."));
    }
    if (takeParsed < 1 || takeParsed > 200)
    {
        validationErrors.Add(("take", "Must be between 1 and 200."));
    }

    if (validationErrors.Count > 0)
    {
        return Results.Json(
            new
            {
                error = new
                {
                    code = "validation_failed",
                    message = "One or more parameters are invalid.",
                    details = validationErrors.Select(e => new { field = e.Field, message = e.Message }).ToArray(),
                },
            },
            statusCode: StatusCodes.Status400BadRequest);
    }

    // Audit-log views are always audited (plan §3.7).
    await auditStore.AppendAsync(new AuditEvent
    {
        TimestampUtc = DateTime.UtcNow,
        Actor = "admin",
        Action = "audit.query",
        Outcome = AuditOutcomes.Success,
        Detail = $"skip={skipParsed};take={takeParsed}",
    }, context.RequestAborted);

    var page = await auditStore.QueryAsync(skipParsed, takeParsed, context.RequestAborted);
    return Results.Json(new
    {
        items = page.Items,
        totalCount = page.TotalCount,
    });
});

// First-admin bootstrap (plan §3.4, D6, task 8): disabled by default — nothing
// works until a LOCAL operator activates a limited initialization window, and
// completion is persisted and irreversible through any API request (T7).
// Local-only authorization validates the actual connection address
// (RemoteIpAddress); forwarded headers are never consulted (D6). All attempts
// are audited. Both endpoints are pre-session; the CSRF/Origin middleware still
// requires an allowed Origin on mutations.
app.MapPost("/api/bootstrap/activate", async (
    HttpContext context,
    BootstrapService bootstrapService,
    IAuditStore auditStore) =>
{
    var nowUtc = DateTime.UtcNow;
    var sourceIp = context.Connection.RemoteIpAddress?.ToString();

    // D6: local-only authorization on the actual connection address.
    if (!LocalAccess.IsLoopbackRemote(context))
    {
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = nowUtc,
            Actor = "anonymous",
            Action = "bootstrap.activate_denied_nonlocal",
            Outcome = AuditOutcomes.Failure,
            SourceIp = sourceIp,
            Detail = null,
        });
        return Results.Json(
            new { error = new { code = "forbidden", message = "Request rejected." } },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (outcome, expiresUtc) = await bootstrapService.ActivateAsync(nowUtc, context.RequestAborted);

    if (outcome == ActivateOutcome.AlreadyCompleted)
    {
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = nowUtc,
            Actor = "anonymous",
            Action = "bootstrap.activate_denied_completed",
            Outcome = AuditOutcomes.Failure,
            SourceIp = sourceIp,
            Detail = null,
        });
        return Results.Json(
            new { error = new { code = "forbidden", message = "Bootstrap is not available." } },
            statusCode: StatusCodes.Status403Forbidden);
    }

    await auditStore.AppendAsync(new AuditEvent
    {
        TimestampUtc = nowUtc,
        Actor = "anonymous",
        Action = "bootstrap.activated",
        Outcome = AuditOutcomes.Success,
        SourceIp = sourceIp,
        Detail = outcome == ActivateOutcome.Opened ? "window_opened" : "window_already_open",
    });

    return Results.Ok(new { status = "window_open", expiresUtc = expiresUtc });
});

app.MapPost("/api/bootstrap/admin", async (
    BootstrapAdminRequest? request,
    HttpContext context,
    BootstrapService bootstrapService,
    IAuditStore auditStore) =>
{
    var nowUtc = DateTime.UtcNow;
    var sourceIp = context.Connection.RemoteIpAddress?.ToString();

    // D6: local-only authorization on the actual connection address.
    if (!LocalAccess.IsLoopbackRemote(context))
    {
        await auditStore.AppendAsync(new AuditEvent
        {
            TimestampUtc = nowUtc,
            Actor = "anonymous",
            Action = "bootstrap.admin_denied_nonlocal",
            Outcome = AuditOutcomes.Failure,
            SourceIp = sourceIp,
            Detail = null,
        });
        return Results.Json(
            new { error = new { code = "forbidden", message = "Request rejected." } },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (request is null || string.IsNullOrEmpty(request.Password))
    {
        return Results.Json(
            new { error = new { code = "validation_failed", message = "A password is required." } },
            statusCode: StatusCodes.Status400BadRequest);
    }

    var outcome = await bootstrapService.CreateAdminAsync(request.Password, nowUtc, context.RequestAborted);

    switch (outcome)
    {
        case CreateAdminOutcome.AlreadyCompleted:
            await auditStore.AppendAsync(new AuditEvent
            {
                TimestampUtc = nowUtc,
                Actor = "anonymous",
                Action = "bootstrap.admin_denied_completed",
                Outcome = AuditOutcomes.Failure,
                SourceIp = sourceIp,
                Detail = null,
            });
            return Results.Json(
                new { error = new { code = "forbidden", message = "Bootstrap is not available." } },
                statusCode: StatusCodes.Status403Forbidden);

        case CreateAdminOutcome.WindowClosed:
            await auditStore.AppendAsync(new AuditEvent
            {
                TimestampUtc = nowUtc,
                Actor = "anonymous",
                Action = "bootstrap.admin_denied_window_closed",
                Outcome = AuditOutcomes.Failure,
                SourceIp = sourceIp,
                Detail = null,
            });
            return Results.Json(
                new { error = new { code = "forbidden", message = "Bootstrap is not available." } },
                statusCode: StatusCodes.Status403Forbidden);

        case CreateAdminOutcome.WeakPassword:
            await auditStore.AppendAsync(new AuditEvent
            {
                TimestampUtc = nowUtc,
                Actor = "anonymous",
                Action = "bootstrap.admin_rejected_weak_password",
                Outcome = AuditOutcomes.Failure,
                SourceIp = sourceIp,
                Detail = null,
            });
            return Results.Json(
                new { error = new { code = "validation_failed", message = "The password does not meet the length requirements." } },
                statusCode: StatusCodes.Status400BadRequest);
    }

    await auditStore.AppendAsync(new AuditEvent
    {
        TimestampUtc = nowUtc,
        Actor = "admin",
        Action = "bootstrap.admin_created",
        Outcome = AuditOutcomes.Success,
        SourceIp = sourceIp,
        Detail = null,
    });

    return Results.Ok(new { status = "admin_created" });
});

// Test-only session lifecycle endpoints (plan task 5, T8–T11). They exercise the
// session lifecycle mechanics alongside the real auth endpoints above.
// Double-gated like the force-error endpoint: configuration flag AND non-production
// environment, DISABLED by default; never reachable in production. They issue no
// credential check — real authentication is task 6.
if (builder.Configuration.GetValue<bool>("Diagnostics:TestSessionsEnabled") &&
    !app.Environment.IsProduction())
{
    // Issues a fresh session: opaque id in the cookie only (never in the body),
    // hash-at-rest in the store (T11). A fresh id per call models rotation on login.
    app.MapPost("/api/test/sessions", async (HttpContext context, SessionOptions options, SecurityOptions security, ISessionStore store) =>
    {
        var (rawId, record) = new SessionService(options).Create("admin", DateTime.UtcNow);
        await store.InsertAsync(record, context.RequestAborted);
        SessionCookieWriter.Issue(context, options.CookieName, rawId, options.AbsoluteLifetime);
        CsrfCookieWriter.Issue(context, security.CsrfCookieName, CsrfCookieWriter.GenerateToken(),
            options.AbsoluteLifetime);
        return Results.Ok(new { status = "created" });
    });

    // Validates the presented cookie against the store: 200 when the session is
    // valid, 401 when unknown/expired/revoked (T8, T9). No data in the 401 body.
    app.MapGet("/api/test/sessions/current", async (HttpContext context, SessionOptions options, ISessionStore store) =>
    {
        if (!context.Request.Cookies.TryGetValue(options.CookieName, out var rawId))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var record = await store.GetByIdHashAsync(SessionService.HashId(rawId), context.RequestAborted);
        if (record is null || !SessionService.IsValid(record, DateTime.UtcNow))
        {
            return Results.Json(new { error = "unauthenticated" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new { status = "valid" });
    });

    // Revokes the presented session immediately and clears the cookie (T9).
    app.MapPost("/api/test/sessions/invalidate", async (HttpContext context, SessionOptions options, ISessionStore store) =>
    {
        if (context.Request.Cookies.TryGetValue(options.CookieName, out var rawId))
        {
            await store.RevokeAsync(SessionService.HashId(rawId), context.RequestAborted);
        }

        SessionCookieWriter.Clear(context, options.CookieName);
        return Results.Ok(new { status = "invalidated" });
    });
}

app.Run();

public partial class Program { }

/// <summary>Login request body. The password is bound but never logged (D7).</summary>
public sealed record LoginRequest(string? Password);

/// <summary>First-admin bootstrap request body. The password is bound but never logged (D7).</summary>
public sealed record BootstrapAdminRequest(string? Password);
