using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Collections.Concurrent;
using GraphMcp.Auth;
using GraphMcp.Graph;
using GraphMcp.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace GraphMcp.Tests.Mcp;

public sealed class McpTestFactory : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "graph-mcp-tests-" + Guid.NewGuid().ToString("N"));
    public FakeGraphServices Graph { get; } = new();
    public Dictionary<string, string?> ConfigurationOverrides { get; } = [];
    public ConcurrentQueue<string> Logs { get; } = new();

    public McpTestFactory()
    {
        Directory.CreateDirectory(directory);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=GraphMcpTests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(Path.Combine(directory, "test.pfx"), cert.Export(X509ContentType.Pfx));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Host configuration reaches the minimal entrypoint before eager certificate loading.
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Microsoft:TenantId"] = "11111111-1111-1111-1111-111111111111",
            ["Microsoft:ClientId"] = "22222222-2222-2222-2222-222222222222",
            ["Microsoft:ExpectedUserObjectId"] = "33333333-3333-3333-3333-333333333333",
            ["Authentication:OperatorBaseUrl"] = "https://operator.test",
            ["Authentication:ClientCertificatePath"] = Path.Combine(directory, "test.pfx"),
            ["Authentication:DataProtectionCertificatePath"] = Path.Combine(directory, "test.pfx"),
            ["Authentication:DataProtectionKeysPath"] = Path.Combine(directory, "keys"),
            ["Authentication:TokenCachePath"] = Path.Combine(directory, "tokens.bin"),
            ["Authentication:OwnerStatePath"] = Path.Combine(directory, "owner.bin"),
            ["Transport:AllowedHosts:0"] = "localhost",
            ["Transport:AllowedHosts:1"] = "operator.test"
        }));
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(ConfigurationOverrides));
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IAccountService>();
            services.RemoveAll<IMailService>();
            services.RemoveAll<ICalendarService>();
            services.RemoveAll<IGraphCredentialProvider>();
            services.AddSingleton<IAccountService>(Graph);
            services.AddSingleton<IMailService>(Graph);
            services.AddSingleton<ICalendarService>(Graph);
            services.AddSingleton<IGraphCredentialProvider, FakeCredentialProvider>();
            services.AddTransient<IStartupFilter, TestListenerFilter>();
            services.RemoveAll<ILoggerProvider>();
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs));
        });
    }

    public async Task<McpClient> ConnectAsync()
    {
        var http = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false
        }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeCredentialProvider : IGraphCredentialProvider
    {
        public string ConnectionGeneration => "test-generation";
        public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken) => Task.FromResult("synthetic-token");
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);
        public void Dispose() { }
        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    // TestServer has no sockets. Populate the connection feature in the test host only.
    private sealed class TestListenerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                context.Connection.LocalPort = context.Request.Headers["X-Test-Local-Port"] == "8081" ? 8081 : 8080;
                context.Connection.RemoteIpAddress = context.Request.Headers["X-Test-Unknown-Proxy"] == "true"
                    ? IPAddress.Parse("198.51.100.2") : IPAddress.Loopback;
                context.Request.Headers.Remove("X-Test-Local-Port");
                context.Request.Headers.Remove("X-Test-Unknown-Proxy");
                await continuation(context);
            });
            next(app);
        };
    }
}

public sealed class FakeGraphServices : IAccountService, IMailService, ICalendarService
{
    public int Calls { get; private set; }
    public object? LastRequest { get; private set; }
    public Exception? Failure { get; set; }
    public Func<CancellationToken, Task<AccountDto>>? AccountHandler { get; set; }
    public string? AccountDisplayName { get; set; } = "Synthetic account";

    private Task<T> Return<T>(T value, object? request = null)
    {
        Calls++;
        LastRequest = request;
        return Failure is null ? Task.FromResult(value) : Task.FromException<T>(Failure);
    }

    public Task<AccountDto> GetMeAsync(CancellationToken cancellationToken) => AccountHandler is { } handler
        ? handler(cancellationToken) : Return(new AccountDto("account-id", AccountDisplayName, "owner@example.com", "owner@example.com"));
    public Task<PageResult<MailSummaryDto>> ListAsync(MailListRequest request, CancellationToken cancellationToken) =>
        Return(new PageResult<MailSummaryDto>([Message], request.Cursor is null ? "opaque-next-cursor" : null), request);
    public Task<MailSearchResult> SearchAsync(MailSearchRequest request, CancellationToken cancellationToken) => Return(new MailSearchResult([Message], false), request);
    public Task<MailMessageDto> GetAsync(MailGetRequest request, CancellationToken cancellationToken) =>
        Return(new MailMessageDto(Message, null, [], [], null, "Synthetic message text", false, [Attachment], false, false), request);
    public Task<AttachmentResult> GetAttachmentAsync(AttachmentRequest request, CancellationToken cancellationToken) =>
        Return(new AttachmentResult(Attachment, request.Representation == "text" ? new("Synthetic attachment text", "utf-8", false) : null, null), request);
    public Task<PageResult<CalendarDto>> ListAsync(PageRequest request, CancellationToken cancellationToken) =>
        Return(new PageResult<CalendarDto>([new("calendar-id", "Calendar", true)], null), request);
    public Task<PageResult<CalendarEventDto>> GetEventsAsync(CalendarEventsRequest request, CancellationToken cancellationToken) =>
        Return(new PageResult<CalendarEventDto>([Event], null), request);
    public Task<CalendarEventDto> GetEventAsync(CalendarEventRequest request, CancellationToken cancellationToken) => Return(Event, request);
    public Task<AvailabilityResult> GetAvailabilityAsync(AvailabilityRequest request, CancellationToken cancellationToken) =>
        Return(new AvailabilityResult(request.Start, request.End, request.TimeZone, request.IntervalMinutes,
            new Dictionary<string, string> { ["0"] = "free or working elsewhere" }, [new("owner@example.com", "00", null)]), request);

    private static MailSummaryDto Message => new("message-id", "Synthetic subject", new("Sender", "sender@example.com"), DateTimeOffset.Parse("2026-10-01T09:00:00Z"), false, true, "Preview");
    private static AttachmentMetadataDto Attachment => new("attachment-id", "notes.txt", "text/plain", 25, false, "file");
    private static CalendarEventDto Event => new("event-id", "Synthetic event", DateTimeOffset.Parse("2026-10-01T09:00:00Z"),
        DateTimeOffset.Parse("2026-10-01T10:00:00Z"), "UTC", false, "UTC", "UTC", null, null, [], false, false, null, false);
}
