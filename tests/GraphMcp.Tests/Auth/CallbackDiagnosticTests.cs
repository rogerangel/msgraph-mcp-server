using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace GraphMcp.Tests.Auth;

public sealed class CallbackDiagnosticTests
{
    [Theory]
    [InlineData("token_validated", "tid", "tenant")]
    [InlineData("token_validated", "oid", "object")]
    [InlineData("token_validated", "utid", "home-tenant")]
    [InlineData("token_validated", "uid", "home-object")]
    [InlineData("ticket_received", "tid", "tenant")]
    [InlineData("ticket_received", "oid", "object")]
    [InlineData("ticket_received", "utid", "home-tenant")]
    [InlineData("ticket_received", "uid", "home-object")]
    public async Task Owner_mismatch_logs_only_the_failed_comparison(string stage, string claim, string comparison)
    {
        using var fixture = new CallbackFixture();
        var principal = fixture.Principal();
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.RemoveClaim(identity.FindFirst(claim)!);
        identity.AddClaim(new Claim(claim, CallbackFixture.WrongIdentifier));

        var failure = await fixture.Invoke(stage, principal);

        Assert.NotNull(failure);
        fixture.AssertDiagnostic(stage, "owner_identity", "mismatch", comparison);
        Assert.Equal(0, fixture.Tokens.Calls);
        Assert.False(fixture.Owner.Connected);
        Assert.False(File.Exists(fixture.OwnerPath));
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("token_validated")]
    [InlineData("ticket_received")]
    public async Task Missing_principal_is_rejected_with_safe_diagnostic(string stage)
    {
        using var fixture = new CallbackFixture();

        var failure = await fixture.Invoke(stage, null);

        Assert.NotNull(failure);
        foreach (var comparison in new[] { "tenant", "object", "home-tenant", "home-object" })
            fixture.AssertDiagnostic(stage, "owner_identity", "mismatch", comparison);
        Assert.Equal(0, fixture.Tokens.Calls);
        Assert.False(fixture.Owner.Connected);
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("User.Read")]
    [InlineData("Mail.ReadWrite")]
    [InlineData("Calendars.Read")]
    public async Task Missing_required_scope_is_distinct_and_does_not_log_scope_contents(string missingScope)
    {
        using var fixture = new CallbackFixture();
        fixture.Tokens.Result = fixture.AuthenticationResult([
            .. GraphScopes.Required.Where(scope => scope != missingScope), "poison-extra-scope"]);

        var failure = await fixture.Invoke("ticket_received", fixture.Principal());

        Assert.NotNull(failure);
        fixture.AssertDiagnostic("ticket_received", "graph_scopes", "mismatch");
        fixture.AssertDiagnostic("ticket_received", "graph_additional_scopes", "present");
        Assert.DoesNotContain(fixture.Diagnostics, entry => entry.Field("Gate") == "owner_persistence");
        Assert.Equal(1, fixture.Tokens.Calls);
        Assert.False(fixture.Owner.Connected);
        Assert.False(File.Exists(fixture.OwnerPath));
        fixture.AssertSafeLogs();
    }

    [Theory]
    [InlineData("Mail.Read")]
    [InlineData("Mail.Send")]
    [InlineData("https://graph.microsoft.com/Calendars.ReadWrite")]
    [InlineData("poison-extra-scope")]
    public async Task Additional_scopes_are_accepted_without_logging_their_contents(string additionalScope)
    {
        using var fixture = new CallbackFixture();
        fixture.Tokens.Result = fixture.AuthenticationResult([.. GraphScopes.Required, additionalScope]);

        Assert.Null(await fixture.Invoke("ticket_received", fixture.Principal()));

        fixture.AssertDiagnostic("ticket_received", "graph_scopes", "approved");
        fixture.AssertDiagnostic("ticket_received", "graph_additional_scopes", "present");
        fixture.AssertDiagnostic("ticket_received", "owner_persistence", "succeeded");
        Assert.True(fixture.Owner.Connected);
        Assert.True(File.Exists(fixture.OwnerPath));
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task Oidc_scopes_do_not_count_as_additional_graph_scopes_or_leak_into_logs()
    {
        using var fixture = new CallbackFixture();
        fixture.Tokens.Result = fixture.AuthenticationResult([
            .. GraphScopes.Required, "openid", "profile", "offline_access", "email"]);

        Assert.Null(await fixture.Invoke("ticket_received", fixture.Principal()));

        fixture.AssertDiagnostic("ticket_received", "graph_scopes", "approved");
        fixture.AssertDiagnostic("ticket_received", "graph_additional_scopes", "absent");
        Assert.True(fixture.Owner.Connected);
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task Home_account_mismatch_is_distinct_and_never_logs_either_identifier()
    {
        using var fixture = new CallbackFixture();
        fixture.Tokens.Result = fixture.AuthenticationResult(GraphScopes.Required, CallbackFixture.WrongHomeAccount);

        var failure = await fixture.Invoke("ticket_received", fixture.Principal());

        Assert.NotNull(failure);
        fixture.AssertDiagnostic("ticket_received", "graph_scopes", "approved");
        fixture.AssertDiagnostic("ticket_received", "home_account", "mismatch");
        Assert.DoesNotContain(fixture.Diagnostics, entry => entry.Field("Gate") == "owner_persistence");
        Assert.False(fixture.Owner.Connected);
        Assert.False(File.Exists(fixture.OwnerPath));
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task Acquisition_exception_is_sanitized_before_framework_failure_handling()
    {
        using var fixture = new CallbackFixture();
        fixture.Tokens.Error = new InvalidOperationException(string.Join(" ", fixture.SensitiveValues));

        var failure = await fixture.Invoke("ticket_received", fixture.Principal());

        Assert.NotNull(failure);
        fixture.AssertDiagnostic("ticket_received", "token_acquisition", "failed");
        Assert.DoesNotContain(fixture.Diagnostics, entry => entry.Field("Gate") == "owner_persistence");
        Assert.False(fixture.Owner.Connected);
        Assert.False(File.Exists(fixture.OwnerPath));
        fixture.AssertContainsNoSensitiveValues(failure.Failure?.ToString() ?? "");
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task Owner_persistence_failure_is_distinct_and_does_not_connect_owner()
    {
        using var fixture = new CallbackFixture();
        // Owner construction already succeeded; an obstructing directory now fails the atomic save.
        Directory.CreateDirectory(fixture.OwnerPath);

        var failure = await fixture.Invoke("ticket_received", fixture.Principal());

        Assert.NotNull(failure);
        fixture.AssertDiagnostic("ticket_received", "graph_scopes", "approved");
        fixture.AssertDiagnostic("ticket_received", "graph_additional_scopes", "absent");
        fixture.AssertDiagnostic("ticket_received", "home_account", "approved");
        fixture.AssertDiagnostic("ticket_received", "owner_persistence", "failed");
        Assert.False(fixture.Owner.Connected);
        fixture.AssertContainsNoSensitiveValues(failure.Failure?.ToString() ?? "");
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task Successful_acceptance_logs_safe_gate_results_and_persists_owner()
    {
        using var fixture = new CallbackFixture();
        var principal = fixture.Principal();

        Assert.Null(await fixture.Invoke("token_validated", principal));
        Assert.Null(await fixture.Invoke("ticket_received", principal));

        fixture.AssertDiagnostic("token_validated", "owner_identity", "approved");
        fixture.AssertDiagnostic("ticket_received", "owner_identity", "approved");
        fixture.AssertDiagnostic("ticket_received", "token_acquisition", "succeeded");
        fixture.AssertDiagnostic("ticket_received", "graph_scopes", "approved");
        fixture.AssertDiagnostic("ticket_received", "graph_additional_scopes", "absent");
        fixture.AssertDiagnostic("ticket_received", "home_account", "approved");
        fixture.AssertDiagnostic("ticket_received", "owner_persistence", "succeeded");
        fixture.AssertDiagnostic("ticket_received", "acceptance", "succeeded");
        Assert.True(fixture.Owner.Connected);
        Assert.True(File.Exists(fixture.OwnerPath));
        Assert.Equal(fixture.Owner.Generation, principal.FindFirstValue(OwnerIdentityStore.GenerationClaim));
        Assert.Equal("/operator", fixture.Ticket!.ReturnUri);
        fixture.AssertSafeLogs();
    }

    [Fact]
    public async Task Remote_failure_retains_generic_http_response_without_sensitive_material()
    {
        using var fixture = new CallbackFixture();
        var context = new RemoteFailureContext(fixture.Http, fixture.Scheme, fixture.Options,
            new InvalidOperationException(string.Join(" ", fixture.SensitiveValues)))
        {
            Properties = fixture.Properties()
        };

        await fixture.Options.Events.OnRemoteFailure(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, fixture.Http.Response.StatusCode);
        Assert.Equal("text/plain", fixture.Http.Response.ContentType);
        Assert.True(context.Result?.Handled);
        var body = Encoding.UTF8.GetString(((MemoryStream)fixture.Http.Response.Body).ToArray());
        Assert.Equal("Microsoft login failed. Start a new login from the operator page.", body);
        fixture.AssertContainsNoSensitiveValues(body);
        fixture.AssertSafeLogs(requireDiagnostic: false);
    }

    [Theory]
    [InlineData("token_validated", false)]
    [InlineData("token_validated", true)]
    [InlineData("ticket_received", false)]
    [InlineData("ticket_received", true)]
    public async Task Earlier_handler_failure_cannot_continue_acceptance_or_leak_exception(string stage, bool throws)
    {
        using var fixture = new CallbackFixture(options =>
        {
            if (stage == "token_validated")
                options.Events.OnTokenValidated = context =>
                {
                    if (throws) throw new InvalidOperationException("poison-id-token poison-auth-code poison-raw-claim");
                    context.Fail("poison-id-token poison-auth-code poison-raw-claim");
                    return Task.CompletedTask;
                };
            else
                options.Events.OnTicketReceived = context =>
                {
                    if (throws) throw new InvalidOperationException("poison-refresh-token poison-cookie poison-state");
                    context.Fail("poison-refresh-token poison-cookie poison-state");
                    return Task.CompletedTask;
                };
        });

        var failure = await fixture.Invoke(stage, fixture.Principal());

        Assert.NotNull(failure);
        fixture.AssertDiagnostic(stage, "preceding_handler", "failed");
        Assert.Equal(0, fixture.Tokens.Calls);
        Assert.False(fixture.Owner.Connected);
        Assert.False(File.Exists(fixture.OwnerPath));
        fixture.AssertContainsNoSensitiveValues(failure.Failure?.ToString() ?? "");
        fixture.AssertSafeLogs();
    }

    private sealed class CallbackFixture : IDisposable
    {
        public const string WrongIdentifier = "44444444-4444-4444-4444-444444444444";
        public const string WrongHomeAccount = "55555555-5555-5555-5555-555555555555.66666666-6666-6666-6666-666666666666";
        private const string DiagnosticCategory = "GraphMcp.Auth.OidcAcceptanceDiagnostics";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "callback-diagnostics-" + Guid.NewGuid().ToString("N"));
        private readonly MicrosoftOptions _microsoft = new()
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ClientId = "22222222-2222-2222-2222-222222222222",
            ExpectedUserObjectId = "33333333-3333-3333-3333-333333333333"
        };
        private readonly CaptureLoggerProvider _logs = new();
        private readonly ServiceProvider _services;
        private readonly string[] _certificateMaterial;
        public OwnerIdentityStore Owner { get; }
        public string OwnerPath => Path.Combine(_directory, "poison-owner-file-path", "owner.bin");
        public TokenProxy Tokens { get; }
        public DefaultHttpContext Http { get; }
        public AuthenticationScheme Scheme { get; } = new(GraphAuthenticationExtensions.OidcScheme, null, typeof(OpenIdConnectHandler));
        public OpenIdConnectOptions Options { get; }
        public TicketReceivedContext? Ticket { get; private set; }
        public IEnumerable<LogEntry> Diagnostics => _logs.Entries.Where(entry => entry.Category == DiagnosticCategory);
        public IReadOnlyList<string> SensitiveValues => [
            "poison-access-token", "poison-refresh-token", "poison-id-token", "poison-auth-code", "poison-nonce",
            "poison-state", "poison-cookie", "poison-owner@example.invalid", "Poison Owner Name", "poison-raw-claim",
            "poison-certificate-material", "poison-owner-file-path", "poison-extra-scope", "poison-mailbox-body",
            _microsoft.TenantId, _microsoft.ClientId, _microsoft.ExpectedUserObjectId, _microsoft.ExpectedHomeAccountId,
            WrongIdentifier, WrongHomeAccount, Owner.Generation,
            "Mail.Read", "Mail.ReadWrite", "Mail.Send", "Calendars.Read", "Calendars.ReadWrite", "User.Read",
            "openid", "profile", "offline_access", "email", "https://graph.microsoft.com/", .. _certificateMaterial
        ];

        public CallbackFixture(Action<OpenIdConnectOptions>? configure = null)
        {
            Directory.CreateDirectory(_directory);
            var certificatePath = Path.Combine(_directory, "test.pfx");
            using var rsa = RSA.Create(2048);
            using var certificate = new CertificateRequest("CN=poison-certificate-material", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificateMaterial = [certificate.ExportCertificatePem(), Convert.ToBase64String(certificate.RawData),
                certificate.Thumbprint, rsa.ExportRSAPrivateKeyPem()];
            File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Microsoft:TenantId"] = _microsoft.TenantId,
                ["Microsoft:ClientId"] = _microsoft.ClientId,
                ["Microsoft:ExpectedUserObjectId"] = _microsoft.ExpectedUserObjectId,
                ["Authentication:OperatorBaseUrl"] = "https://operator.example:9443",
                ["Authentication:ClientCertificatePath"] = certificatePath,
                ["Authentication:DataProtectionCertificatePath"] = certificatePath,
                ["Authentication:DataProtectionKeysPath"] = Path.Combine(_directory, "keys"),
                ["Authentication:TokenCachePath"] = Path.Combine(_directory, "cache.bin"),
                ["Authentication:OwnerStatePath"] = OwnerPath
            }).Build();
            var services = new ServiceCollection().AddLogging(logging => logging.AddProvider(_logs));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddGraphAuthentication(configuration);
            if (configure is not null) services.Configure(GraphAuthenticationExtensions.OidcScheme, configure);
            var acquisition = DispatchProxy.Create<ITokenAcquisition, TokenProxy>();
            Tokens = (TokenProxy)acquisition;
            Tokens.Result = AuthenticationResult(GraphScopes.Required);
            services.Replace(ServiceDescriptor.Singleton<ITokenAcquisition>(acquisition));
            _services = services.BuildServiceProvider();
            Options = _services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(GraphAuthenticationExtensions.OidcScheme);
            Owner = _services.GetRequiredService<OwnerIdentityStore>();
            Http = new DefaultHttpContext { RequestServices = _services };
            Http.Response.Body = new MemoryStream();
            Http.Request.Headers.Authorization = "Bearer poison-access-token";
            Http.Request.Headers.Cookie = "operator=poison-cookie";
            Http.Request.QueryString = new QueryString("?code=poison-auth-code&state=poison-state");
            _logs.Entries.Clear();
        }

        public ClaimsPrincipal Principal() => new(new ClaimsIdentity([
            new Claim("tid", _microsoft.TenantId), new Claim("oid", _microsoft.ExpectedUserObjectId),
            new Claim("utid", _microsoft.TenantId), new Claim("uid", _microsoft.ExpectedUserObjectId),
            new Claim("email", "poison-owner@example.invalid"), new Claim("name", "Poison Owner Name"),
            new Claim("untrusted_claim", "poison-raw-claim"), new Claim("mailbox_body", "poison-mailbox-body")
        ], GraphAuthenticationExtensions.OidcScheme));

        public AuthenticationProperties Properties()
        {
            var properties = new AuthenticationProperties();
            properties.Items["nonce"] = "poison-nonce";
            properties.Items["state"] = "poison-state";
            properties.StoreTokens([
                new AuthenticationToken { Name = "access_token", Value = "poison-access-token" },
                new AuthenticationToken { Name = "refresh_token", Value = "poison-refresh-token" },
                new AuthenticationToken { Name = "id_token", Value = "poison-id-token" }
            ]);
            return properties;
        }

        public AuthenticationResult AuthenticationResult(IEnumerable<string> scopes, string? homeAccount = null) => new(
            "poison-access-token", false, "Bearer", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1),
            _microsoft.TenantId, new TestAccount(homeAccount ?? _microsoft.ExpectedHomeAccountId), "poison-id-token", scopes, Guid.NewGuid());

        public async Task<HandleRequestResult?> Invoke(string stage, ClaimsPrincipal? principal)
        {
            if (stage == "token_validated")
            {
                var context = new TokenValidatedContext(Http, Scheme, Options, principal!, Properties())
                {
                    Nonce = "poison-nonce",
                    ProtocolMessage = new OpenIdConnectMessage { Code = "poison-auth-code", State = "poison-state", IdToken = "poison-id-token" },
                    TokenEndpointResponse = new OpenIdConnectMessage
                    {
                        AccessToken = "poison-access-token", RefreshToken = "poison-refresh-token", IdToken = "poison-id-token"
                    }
                };
                await Options.Events.OnTokenValidated(context);
                return context.Result;
            }

            Ticket = new TicketReceivedContext(Http, Scheme, Options,
                new AuthenticationTicket(principal ?? new ClaimsPrincipal(), Properties(), Scheme.Name));
            Ticket.Principal = principal;
            await Options.Events.OnTicketReceived(Ticket);
            if (Ticket.Result is not null)
            {
                Assert.True(Ticket.Result.Handled);
                Assert.Null(Ticket.Result.Failure);
                Assert.Equal(StatusCodes.Status401Unauthorized, Http.Response.StatusCode);
                Assert.Equal("text/plain", Http.Response.ContentType);
                var body = Encoding.UTF8.GetString(((MemoryStream)Http.Response.Body).ToArray());
                Assert.Equal("Microsoft login failed. Start a new login from the operator page.", body);
                AssertContainsNoSensitiveValues(body);
            }
            return Ticket.Result;
        }

        public void AssertDiagnostic(string stage, string gate, string status, string comparison = "none") =>
            Assert.Contains(Diagnostics, entry => entry.Field("Stage") == stage && entry.Field("Gate") == gate
                && entry.Field("Status") == status && entry.Field("Comparison") == comparison);

        public void AssertSafeLogs(bool requireDiagnostic = true)
        {
            if (requireDiagnostic) Assert.NotEmpty(Diagnostics);
            foreach (var entry in Diagnostics)
            {
                Assert.Equal(4100, entry.EventId.Id);
                Assert.Null(entry.Exception);
                Assert.Equal(new[] { "Comparison", "Gate", "Stage", "Status", "{OriginalFormat}" },
                    entry.Fields.Select(field => field.Key).Order(StringComparer.Ordinal));
                Assert.Contains(entry.Field("Stage"), new[] { "token_validated", "ticket_received", "remote_failure" });
                Assert.Contains(entry.Field("Gate"), new[] { "owner_identity", "graph_scopes", "graph_additional_scopes", "home_account", "token_acquisition", "owner_persistence", "acceptance", "preceding_handler" });
                Assert.Contains(entry.Field("Status"), new[] { "approved", "mismatch", "present", "absent", "started", "succeeded", "failed", "stopped" });
                Assert.Contains(entry.Field("Comparison"), new[] { "tenant", "object", "home-tenant", "home-object", "none" });
                Assert.All(entry.Fields, field => Assert.IsType<string>(field.Value));
            }
            foreach (var entry in _logs.Entries)
            {
                AssertContainsNoSensitiveValues(entry.Message);
                AssertContainsNoSensitiveValues(entry.Exception?.ToString() ?? "");
                foreach (var field in entry.Fields)
                {
                    AssertContainsNoSensitiveValues(field.Key);
                    AssertContainsNoSensitiveValues(field.Value?.ToString() ?? "");
                }
            }
            foreach (var scope in _logs.Scopes)
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> fields)
                    foreach (var field in fields)
                    {
                        AssertContainsNoSensitiveValues(field.Key);
                        AssertContainsNoSensitiveValues(field.Value?.ToString() ?? "");
                    }
                AssertContainsNoSensitiveValues(scope.ToString() ?? "");
            }
        }

        public void AssertContainsNoSensitiveValues(string value)
        {
            foreach (var sensitive in SensitiveValues) Assert.DoesNotContain(sensitive, value, StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _services.Dispose();
            Http.Response.Body.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    public class TokenProxy : DispatchProxy
    {
        public AuthenticationResult? Result { get; set; }
        public Exception? Error { get; set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != "GetAuthenticationResultForUserAsync") throw new NotSupportedException();
            Calls++;
            return Error is null ? Task.FromResult(Result!) : Task.FromException<AuthenticationResult>(Error);
        }
    }

    private sealed class TestAccount(string identifier) : IAccount
    {
        public string Username => "poison-owner@example.invalid";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId => new(identifier);
    }

    private sealed record LogEntry(string Category, EventId EventId, string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Fields, Exception? Exception)
    {
        public string? Field(string key) => Fields.SingleOrDefault(field => field.Key == key).Value?.ToString();
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ConcurrentQueue<object> Scopes { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries, Scopes);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<LogEntry> entries, ConcurrentQueue<object> scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            scopes.Enqueue(state);
            return null;
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, eventId, formatter(state, exception),
                state is IEnumerable<KeyValuePair<string, object?>> fields ? fields.ToArray() : [new("state", state)], exception));
    }
}
