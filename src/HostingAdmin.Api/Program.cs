using System.Text.Json;
using HostingAdmin.Api.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Structured logging: JSON console formatter so log records are machine-parseable
// (plan §4). Secret redaction is applied in the exception middleware and must also
// be applied by any code that logs user-influenced state (D7).
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});

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

app.Run();

public partial class Program { }
