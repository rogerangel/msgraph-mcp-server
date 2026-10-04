using System.Net;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using GraphMcp.Graph;
using GraphMcp.Health;
using GraphMcp.Tools;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

// The chiseled image has no shell/curl. This mode only probes liveness and never opens the cache.
if (args is ["--healthcheck"])
{
    var port = Environment.GetEnvironmentVariable("Transport__McpPort") ?? "8080";
    if (!int.TryParse(port, out var number) || number is < 1024 or > 65535) return 1;
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.GetAsync($"http://127.0.0.1:{number}/health/live");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException) { return 1; }
    catch (OperationCanceledException) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
var transport = builder.Configuration.GetSection("Transport").Get<TransportOptions>() ?? new();
builder.Services.AddOptions<TransportOptions>().BindConfiguration("Transport")
    .Validate(o => o.McpPort is >= 1024 and <= 65535 && o.OperatorPort is >= 1024 and <= 65535
        && o.McpPort != o.OperatorPort, "Distinct unprivileged MCP and operator ports are required.")
    .Validate(o => o.MaxRequestBytes is >= 1024 and <= 262_144, "MCP request limit must be 1–256 KiB.")
    .Validate(o => o.AllowedHosts.Length > 0 && o.AllowedHosts.All(h => Uri.CheckHostName(h) != UriHostNameType.Unknown),
        "AllowedHosts must contain exact hostnames or IP addresses, never wildcards.")
    .Validate(o => o.KnownProxies.All(p => IPAddress.TryParse(p, out _)), "KnownProxies must contain IP addresses.")
    .ValidateOnStart();

builder.WebHost.ConfigureKestrel(o =>
{
    o.AddServerHeader = false;
    o.Limits.MaxRequestBodySize = transport.MaxRequestBytes;
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    o.ListenAnyIP(transport.McpPort);
    o.ListenAnyIP(transport.OperatorPort);
});

// Framework request/exception logging can contain OAuth codes, queries, or tool content.
// Keep only explicitly shaped application events in production, including on failure paths.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter((category, level) => category?.StartsWith("GraphMcp.", StringComparison.Ordinal) == true
    && level >= LogLevel.Information);

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    foreach (var proxy in transport.KnownProxies) o.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.AddGraphAuthentication(builder.Configuration);
builder.Services.AddGraphServices(builder.Configuration);
builder.Services.AddSingleton<AuthenticationReadinessState>();
builder.Services.AddHealthChecks().AddCheck<AuthenticationReadinessCheck>("authentication", tags: ["ready"]);
builder.Services.AddSingleton<ToolExecutor>();
builder.Services.AddMcpServer().WithHttpTransport(o =>
{
    o.SessionMode = HttpServerSessionMode.Stateless;
    // Legacy SSE stays disabled (the SDK default); the old enabling API is obsolete.
}).WithTools<AccountTools>().WithTools<MailTools>().WithTools<CalendarTools>().WithTools<DraftTools>();

var app = builder.Build();
_ = app.Services.GetRequiredService<IOptions<TransportOptions>>().Value;
_ = app.Services.GetRequiredService<OwnerIdentityStore>();
ToolSchemas.Apply(app.Services.GetServices<McpServerTool>());
var operatorOptions = app.Services.GetRequiredService<IOptions<AuthenticationOptions>>().Value;
var operatorHost = new Uri(operatorOptions.OperatorBaseUrl).Host;
var hostLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GraphMcp.Host");

// This gate must precede OIDC middleware. LocalPort cannot be supplied in a forwarding header.
app.Use(async (context, next) =>
{
    var port = context.Connection.LocalPort;
    var path = context.Request.Path;
    var mcp = port == transport.McpPort;
    var op = port == transport.OperatorPort;
    var allowed = mcp && (path == "/mcp" || path == "/health/live" || path == "/health/ready")
        || op && path.StartsWithSegments("/operator");
    if (!allowed) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    var host = context.Request.Host.Host;
    if (!transport.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
        && !(op && host.Equals(operatorHost, StringComparison.OrdinalIgnoreCase)))
    { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    if (mcp && context.Request.Headers.TryGetValue("Origin", out var origins)
        && (origins.Count != 1 || !transport.AllowedOrigins.Contains(origins[0], StringComparer.Ordinal)))
    { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    try { await next(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception)
    {
        // Do not let development exception pages or framework logs expose sensitive responses.
        hostLogger.LogError("Unhandled request failure on {Listener}", mcp ? "mcp" : "operator");
        if (!context.Response.HasStarted)
        {
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("Request failed.");
        }
        else context.Abort();
    }
});
// An empty proxy allowlist must NOT activate ASP.NET's trust-all behavior.
if (transport.KnownProxies.Length > 0) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == transport.OperatorPort && !context.Request.IsHttps)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("The operator interface requires HTTPS through the configured trusted proxy.");
        return;
    }
    await next(context);
});

// A terminal branch prevents MCP/health requests from ever entering OIDC authentication.
app.MapWhen(context => context.Connection.LocalPort == transport.McpPort, branch =>
{
    branch.UseRouting();
    branch.UseEndpoints(endpoints =>
    {
        endpoints.MapMcp("/mcp");
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = (context, report) => context.Response.WriteAsync(
                report.Status == HealthStatus.Healthy ? "Ready" : "Not ready")
        });
    });
});
app.UseGraphOperatorAuthentication();
app.UseRouting();
#pragma warning disable ASP0014 // Operator and MCP routing are intentionally isolated by terminal branch.
app.UseEndpoints(endpoints => endpoints.MapOperatorEndpoints());
#pragma warning restore ASP0014
await app.RunAsync();
return 0;

public partial class Program;
