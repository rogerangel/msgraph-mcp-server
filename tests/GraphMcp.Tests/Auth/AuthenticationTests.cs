using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using AuthOptions = GraphMcp.Configuration.AuthenticationOptions;

namespace GraphMcp.Tests.Auth;

public sealed class AuthenticationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "graph-auth-" + Guid.NewGuid().ToString("N"));
    private readonly MicrosoftOptions _microsoft = new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ClientId = "22222222-2222-2222-2222-222222222222",
        ExpectedUserObjectId = "33333333-3333-3333-3333-333333333333"
    };
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private AuthOptions Settings => new()
    {
        TokenCachePath = Path.Combine(_directory, "cache.bin"),
        OwnerStatePath = Path.Combine(_directory, "owner.bin")
    };
    private OwnerIdentityStore Owner(IDataProtectionProvider? protection = null) =>
        new(Options.Create(_microsoft), Options.Create(Settings), protection ?? _protection);
    private TestCache Cache(IDataProtectionProvider? protection = null) => new(Settings, _microsoft, protection ?? _protection);
    private ClaimsPrincipal Principal(string? userId = null) => new(new ClaimsIdentity([
        new Claim("oid", userId ?? _microsoft.ExpectedUserObjectId), new Claim("tid", _microsoft.TenantId),
        new Claim("uid", userId ?? _microsoft.ExpectedUserObjectId), new Claim("utid", _microsoft.TenantId)
    ], "test"));

    [Fact]
    public async Task Cache_is_ciphertext_and_survives_provider_recreation()
    {
        var bytes = Encoding.UTF8.GetBytes("synthetic-refresh-token-never-real");
        await Cache().Write(_microsoft.ExpectedHomeAccountId, bytes);
        Assert.DoesNotContain("synthetic-refresh-token", Encoding.UTF8.GetString(File.ReadAllBytes(Settings.TokenCachePath)));
        Assert.Equal(bytes, await Cache().Read(_microsoft.ExpectedHomeAccountId));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Settings.TokenCachePath));
    }

    [Fact]
    public async Task Cache_rejects_plaintext_wrong_key_and_other_account()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Settings.TokenCachePath, "plaintext synthetic cache");
        await Assert.ThrowsAsync<CryptographicException>(() => Cache().Read(_microsoft.ExpectedHomeAccountId));
        await Cache().Write(_microsoft.ExpectedHomeAccountId, [1, 2, 3]);
        await Assert.ThrowsAsync<CryptographicException>(() => Cache(new EphemeralDataProtectionProvider()).Read(_microsoft.ExpectedHomeAccountId));
        await Assert.ThrowsAsync<GraphAuthenticationException>(() => Cache().Write("wrong.account", [7]));
        Assert.Equal(new byte[] { 1, 2, 3 }, await Cache().Read(_microsoft.ExpectedHomeAccountId));
    }

    [Fact]
    public async Task Clearing_cache_does_not_leave_persisted_tokens()
    {
        await Cache().Write(_microsoft.ExpectedHomeAccountId, [1, 2, 3]);
        await Cache().ClearAsync(_microsoft.ExpectedHomeAccountId);
        Assert.Null(await Cache().Read(_microsoft.ExpectedHomeAccountId));
    }

    [Fact]
    public void Owner_state_restarts_without_cookie_and_disconnect_invalidates_generation()
    {
        string generation;
        using (var owner = Owner())
        {
            Assert.False(owner.Connected);
            owner.Connect(Principal());
            generation = owner.Generation;
        }
        using (var restarted = Owner())
        {
            Assert.True(restarted.Connected);
            Assert.Equal(generation, restarted.Generation);
            Assert.Equal(_microsoft.ExpectedHomeAccountId, restarted.GetPrincipal().GetMsalAccountId());
            restarted.Disconnect();
            Assert.NotEqual(generation, restarted.Generation);
        }
        using var disconnected = Owner();
        Assert.False(disconnected.Connected);
        Assert.Throws<GraphAuthenticationException>(disconnected.GetPrincipal);
    }

    [Fact]
    public void Owner_rejects_guest_or_wrong_account_and_second_writer()
    {
        using var owner = Owner();
        Assert.Throws<GraphAuthenticationException>(() => owner.Connect(Principal(Guid.NewGuid().ToString())));
        var guest = Principal();
        var identity = (ClaimsIdentity)guest.Identity!;
        identity.RemoveClaim(identity.FindFirst("utid")!);
        identity.AddClaim(new Claim("utid", Guid.NewGuid().ToString()));
        Assert.False(owner.IsExpectedOwner(guest));
        Assert.Throws<IOException>(() => Owner());
    }

    [Fact]
    public void Failed_connect_write_never_marks_owner_connected()
    {
        using var owner = Owner();
        // A directory at the target file location makes atomic file replacement fail on all platforms.
        Directory.CreateDirectory(Settings.OwnerStatePath);
        Assert.ThrowsAny<IOException>(() => owner.Connect(Principal()));
        Assert.False(owner.Connected);
    }

    [Fact]
    public void Failed_disconnect_storage_operation_still_invalidates_memory()
    {
        using var owner = Owner();
        owner.Connect(Principal());
        var generation = owner.Generation;
        File.Delete(Settings.OwnerStatePath);
        Directory.CreateDirectory(Settings.OwnerStatePath);
        Assert.ThrowsAny<Exception>(owner.Disconnect);
        Assert.False(owner.Connected);
        Assert.NotEqual(generation, owner.Generation);
        Assert.Throws<GraphAuthenticationException>(owner.GetPrincipal);
    }

    [Theory]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read", true, false)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read openid profile offline_access email", true, false)]
    [InlineData("https://graph.microsoft.com/User.Read https://graph.microsoft.com/Mail.ReadWrite https://graph.microsoft.com/Calendars.Read", true, false)]
    [InlineData("HTTPS://GRAPH.MICROSOFT.COM/user.read MAIL.READWRITE calendars.read OPENID PROFILE OFFLINE_ACCESS EMAIL", true, false)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read User.Read https://graph.microsoft.com/Mail.ReadWrite", true, false)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read Mail.Read", true, true)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read Mail.Send", true, true)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read https://graph.microsoft.com/Calendars.ReadWrite", true, true)]
    [InlineData("Mail.ReadWrite Calendars.Read", false, false)]
    [InlineData("User.Read Calendars.Read", false, false)]
    [InlineData("User.Read Mail.ReadWrite", false, false)]
    [InlineData("User.Read Mail.Read Calendars.Read", false, true)]
    [InlineData("openid profile offline_access email", false, false)]
    [InlineData("Mail.ReadWrite Calendars.Read https://another.example/User.Read", false, true)]
    [InlineData("User.Read Calendars.Read https://graph.microsoft.com.evil.example/Mail.ReadWrite", false, true)]
    public void Token_scopes_must_contain_required_graph_scopes(string scopes, bool accepted, bool hasAdditionalScopes)
    {
        Assert.Equal(accepted, GraphScopes.ContainsRequiredScopes(scopes.Split(' ')));
        Assert.Equal(accepted, GraphScopes.ContainsRequiredScopes(scopes.Split(' '), out var actualAdditionalScopes));
        Assert.Equal(hasAdditionalScopes, actualAdditionalScopes);
    }

    [Theory]
    [InlineData("https://operator.example", true)]
    [InlineData("https://operator.example:9443", true)]
    [InlineData("https://operator.example/proxy", false)]
    [InlineData("http://operator.example", false)]
    [InlineData("https://user:password@operator.example", false)]
    public void Public_operator_origin_has_no_fixed_port(string value, bool valid) =>
        Assert.Equal(valid, new AuthOptions { OperatorBaseUrl = value }.HasValidOperatorOrigin());

    [Fact]
    public async Task Token_acquisition_binds_owner_and_propagates_force_refresh_and_cancellation()
    {
        using var owner = Owner();
        owner.Connect(Principal());
        var tokens = DispatchProxy.Create<ITokenAcquisition, TokenProxy>();
        var proxy = (TokenProxy)tokens;
        proxy.Result = Result(GraphScopes.Required);
        var provider = new GraphCredentialProvider(tokens, owner, Options.Create(_microsoft));
        using var cancellation = new CancellationTokenSource();
        Assert.Equal("synthetic-access-token", await provider.GetTokenAsync(true, cancellation.Token));
        Assert.True(proxy.Options!.ForceRefresh);
        Assert.Equal(cancellation.Token, proxy.Options.CancellationToken);
        Assert.Equal(_microsoft.ExpectedHomeAccountId, proxy.Principal!.GetMsalAccountId());
        Assert.Equal(new[] { "User.Read", "Mail.ReadWrite", "Calendars.Read" }, proxy.RequestedScopes);
        proxy.Result = Result([.. GraphScopes.Required, "Mail.Send"]);
        Assert.Equal("synthetic-access-token", await provider.GetTokenAsync(false, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "User.Read", "Mail.ReadWrite", "Calendars.Read" }, proxy.RequestedScopes);
        proxy.Error = new MsalUiRequiredException("test", "sensitive detail");
        var error = await Assert.ThrowsAsync<GraphAuthenticationException>(() => provider.GetTokenAsync(false, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("sensitive", error.ToString());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetTokenAsync(false, cancellation.Token));
    }

    [Theory]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read", true)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read Mail.Read Mail.Send", true)]
    [InlineData("User.Read Mail.ReadWrite Calendars.Read openid profile offline_access email", true)]
    [InlineData("Mail.ReadWrite Calendars.Read", false)]
    [InlineData("User.Read Calendars.Read", false)]
    [InlineData("User.Read Mail.ReadWrite", false)]
    public async Task Silent_token_acquisition_checks_required_scopes_without_requesting_extras(string scopes, bool accepted)
    {
        using var owner = Owner();
        owner.Connect(Principal());
        var tokens = DispatchProxy.Create<ITokenAcquisition, TokenProxy>();
        var proxy = (TokenProxy)tokens;
        proxy.Result = Result(scopes.Split(' '));
        var provider = new GraphCredentialProvider(tokens, owner, Options.Create(_microsoft));

        if (accepted)
            Assert.Equal("synthetic-access-token", await provider.GetTokenAsync(false, TestContext.Current.CancellationToken));
        else
            Assert.Equal("authentication_required", (await Assert.ThrowsAsync<GraphAuthenticationException>(
                () => provider.GetTokenAsync(false, TestContext.Current.CancellationToken))).Code);

        Assert.Equal(new[] { "User.Read", "Mail.ReadWrite", "Calendars.Read" }, proxy.RequestedScopes);
    }

    [Fact]
    public async Task Lifecycle_gate_prevents_logout_from_racing_a_token_acquisition()
    {
        using var owner = Owner();
        owner.Connect(Principal());
        var tokens = DispatchProxy.Create<ITokenAcquisition, TokenProxy>();
        var proxy = (TokenProxy)tokens;
        var pending = new TaskCompletionSource<AuthenticationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.Pending = pending.Task;
        var provider = new GraphCredentialProvider(tokens, owner, Options.Create(_microsoft));
        var acquire = provider.GetTokenAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(0, owner.Gate.CurrentCount);
        var logoutLock = owner.Gate.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(logoutLock.IsCompleted);
        pending.SetResult(Result(GraphScopes.Required));
        await acquire;
        await logoutLock;
        try { owner.Disconnect(); } finally { owner.Gate.Release(); }
        await Assert.ThrowsAsync<GraphAuthenticationException>(() => provider.GetTokenAsync(false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oidc_uses_external_callback_exact_scopes_and_rejects_stale_callback_before_redemption()
    {
        Directory.CreateDirectory(_directory);
        var certificatePath = Path.Combine(_directory, "test.pfx");
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=unit-test-only", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Microsoft:TenantId"] = _microsoft.TenantId, ["Microsoft:ClientId"] = _microsoft.ClientId,
            ["Microsoft:ExpectedUserObjectId"] = _microsoft.ExpectedUserObjectId,
            ["Authentication:OperatorBaseUrl"] = "https://operator.example:9443",
            ["Authentication:ClientCertificatePath"] = certificatePath,
            ["Authentication:DataProtectionCertificatePath"] = certificatePath,
            ["Authentication:DataProtectionKeysPath"] = Path.Combine(_directory, "keys"),
            ["Authentication:TokenCachePath"] = Settings.TokenCachePath,
            ["Authentication:OwnerStatePath"] = Settings.OwnerStatePath
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddGraphAuthentication(configuration);
        using var container = services.BuildServiceProvider();
        var oidc = container.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(GraphAuthenticationExtensions.OidcScheme);
        Assert.Equal("code", oidc.ResponseType);
        Assert.True(oidc.UsePkce);
        Assert.False(oidc.SaveTokens);
        Assert.Equal(new[] { "Calendars.Read", "Mail.ReadWrite", "User.Read", "offline_access", "openid", "profile" }, oidc.Scope.Order(StringComparer.Ordinal).ToArray());
        var msal = container.GetRequiredService<IOptionsMonitor<ConfidentialClientApplicationOptions>>().Get(GraphAuthenticationExtensions.OidcScheme);
        Assert.Equal("https://operator.example:9443/operator/signin-oidc", msal.RedirectUri);

        var scheme = new AuthenticationScheme(GraphAuthenticationExtensions.OidcScheme, null, typeof(OpenIdConnectHandler));
        var httpContext = new DefaultHttpContext { RequestServices = container };
        var redirect = new RedirectContext(httpContext, scheme, oidc, new AuthenticationProperties())
        {
            ProtocolMessage = new() { RedirectUri = "https://untrusted.example/wrong" }
        };
        await oidc.Events.OnRedirectToIdentityProvider(redirect);
        Assert.Equal(msal.RedirectUri, redirect.ProtocolMessage.RedirectUri);

        var expired = new AuthenticationProperties();
        expired.Items[OwnerIdentityStore.GenerationProperty] = "expired-generation";
        var callback = new AuthorizationCodeReceivedContext(httpContext, scheme, oidc, expired);
        await oidc.Events.OnAuthorizationCodeReceived(callback);
        Assert.NotNull(callback.Result?.Failure);

        // Generate a real wrapped DP key ring; another provider with the same wrapping certificate can reopen it.
        var protectedValue = container.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Protect("synthetic cache");
        var keyDirectory = new DirectoryInfo(Path.Combine(_directory, "keys"));
        Assert.Contains("encryptedSecret", File.ReadAllText(Assert.Single(keyDirectory.GetFiles("key-*.xml")).FullName));
        var reopened = DataProtectionProvider.Create(keyDirectory, b => b.SetApplicationName("GraphMcp").ProtectKeysWithCertificate(certificate));
        Assert.Equal("synthetic cache", reopened.CreateProtector("restart-test").Unprotect(protectedValue));
    }

    private AuthenticationResult Result(IEnumerable<string> scopes) => new("synthetic-access-token", false, "Bearer",
        DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(1), _microsoft.TenantId,
        new TestAccount(_microsoft.ExpectedHomeAccountId), "", scopes, Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestCache(AuthOptions options, MicrosoftOptions microsoft, IDataProtectionProvider protection)
        : ProtectedFileTokenCache(Options.Create(options), Options.Create(microsoft), protection)
    {
        public Task Write(string key, byte[] data) => WriteCacheBytesAsync(key, data);
        public Task<byte[]?> Read(string key) => ReadCacheBytesAsync(key);
    }

    public class TokenProxy : DispatchProxy
    {
        public AuthenticationResult? Result { get; set; }
        public Task<AuthenticationResult>? Pending { get; set; }
        public Exception? Error { get; set; }
        public TokenAcquisitionOptions? Options { get; private set; }
        public ClaimsPrincipal? Principal { get; private set; }
        public string[]? RequestedScopes { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != "GetAuthenticationResultForUserAsync") throw new NotSupportedException();
            Principal = args!.OfType<ClaimsPrincipal>().Single();
            Options = args!.OfType<TokenAcquisitionOptions>().Single();
            RequestedScopes = args!.OfType<IEnumerable<string>>().Single().ToArray();
            if (Error is not null) return Task.FromException<AuthenticationResult>(Error);
            return Pending ?? Task.FromResult(Result!);
        }
    }

    private sealed class TestAccount(string identifier) : IAccount
    {
        public string Username => "owner@example.invalid";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId => new(identifier);
    }
}
