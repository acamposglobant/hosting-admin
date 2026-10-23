using System.Runtime.InteropServices;
using HostingAdmin.Agent;

var builder = Host.CreateApplicationBuilder(args);

// Startup validation (D22): the agent fails fast on invalid configuration
// rather than degrading at request time. Same posture as the API's options
// validation in Increment 1.
builder.Services
    .AddOptions<AgentOptions>()
    .Bind(builder.Configuration.GetSection(AgentOptions.SectionName))
    .Validate(o => AgentOptionsValidator.Validate(o).Count == 0
                   && UnitAllowlist.Validate(o.UnitNames).Count == 0,
        "Invalid agent configuration (see AgentOptionsValidator and UnitAllowlist rules).")
    .ValidateOnStart();

builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
});

// D17: on Windows dev the agent is not run — the API uses a null channel
// instead. The agent process itself is Linux-only; building it on Windows is
// still required (CI compiles the full solution on both platforms).
if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
{
    Console.Error.WriteLine(
        "The HostingAdmin agent only runs on Linux (unix domain socket). " +
        "On Windows development, the API uses NullAgentChannel (D17).");
    return;
}

// Host-only validation: the secret file must exist before the socket is bound.
var agentOptions = builder.Configuration
    .GetSection(AgentOptions.SectionName)
    .Get<AgentOptions>() ?? new AgentOptions();
var hostErrors = AgentOptionsValidator.ValidateOnHost(agentOptions);
if (hostErrors.Count > 0)
{
    foreach (var error in hostErrors)
    {
        Console.Error.WriteLine($"Agent configuration error: {error}");
    }
    return;
}

var secret = AgentOptionsValidator.LoadSecret(agentOptions.SecretFile);
builder.Services.AddSingleton(secret);
builder.Services.AddSingleton(new UnitAllowlist(agentOptions.UnitNames));
builder.Services.AddSingleton<ISystemctlRunner, SystemctlRunner>();
builder.Services.AddSingleton<AgentSocketListener>();
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();
host.Run();
