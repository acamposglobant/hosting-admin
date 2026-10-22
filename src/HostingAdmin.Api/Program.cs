using System.Text.Json;
using HostingAdmin.Api.Middleware;
using HostingAdmin.Api.Sessions;
using HostingAdmin.Core.Audit;
using HostingAdmin.Core.Auth;
using HostingAdmin.Core.Sessions;
using HostingAdmin.Infrastructure.Persistence;
using SessionOptions = HostingAdmin.Core.Sessions.SessionOptions;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddSingleton<AuditOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value);
builder.Services.AddSingleton<SessionOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionOptions>>().Value);
builder.Services.AddSingleton<AuthOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value);
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<IAuditStore, SqliteAuditStore>();
builder.Services.AddSingleton<ISessionStore, SqliteSessionStore>();
builder.Services.AddSingleton<IAdminCredentialStore, SqliteAdminCredentialStore>();
builder.Services.AddSingleton<ILoginThrottleStore, SqliteLoginThrottleStore>();
builder.Services.AddSingleton<LoginService>();
builder.Services.AddHostedService<AuditRetentionPruneService>();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<NotFoundEnvelopeMiddleware>();

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
    var (rawId, record) = new SessionService(sessionOptions).Create("admin", nowUtc);
    await sessionStore.InsertAsync(record, context.RequestAborted);
    SessionCookieWriter.Issue(context, sessionOptions.CookieName, rawId, sessionOptions.AbsoluteLifetime);

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
    app.MapPost("/api/test/sessions", async (HttpContext context, SessionOptions options, ISessionStore store) =>
    {
        var (rawId, record) = new SessionService(options).Create("admin", DateTime.UtcNow);
        await store.InsertAsync(record, context.RequestAborted);
        SessionCookieWriter.Issue(context, options.CookieName, rawId, options.AbsoluteLifetime);
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
