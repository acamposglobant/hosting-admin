using System.Text.Json;
using HostingAdmin.Api.Middleware;
using HostingAdmin.Api.Sessions;
using HostingAdmin.Core.Audit;
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

builder.Services.AddSingleton<AuditOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuditOptions>>().Value);
builder.Services.AddSingleton<SessionOptions>(sp =>
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SessionOptions>>().Value);
builder.Services.AddSingleton<SqliteConnectionFactory>();
builder.Services.AddSingleton<IAuditStore, SqliteAuditStore>();
builder.Services.AddSingleton<ISessionStore, SqliteSessionStore>();
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

// Test-only session lifecycle endpoints (plan task 5, T8–T11). They exercise the
// cookie issuance/invalidation mechanics that the real /api/auth/login and
// /api/auth/logout endpoints (task 6) will reuse via SessionCookieWriter.
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
