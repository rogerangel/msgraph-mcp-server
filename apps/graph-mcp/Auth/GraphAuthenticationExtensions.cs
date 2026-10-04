using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.TokenCacheProviders;
using AuthOptions = GraphMcp.Configuration.AuthenticationOptions;

namespace GraphMcp.Auth;

public static class GraphAuthenticationExtensions
{
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static IServiceCollection AddGraphAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MicrosoftOptions>().Bind(configuration.GetSection("Microsoft"))
            .Validate(o => Guid.TryParse(o.TenantId, out _) && Guid.TryParse(o.ClientId, out _)
                && Guid.TryParse(o.ExpectedUserObjectId, out _), "Microsoft tenant, client and owner IDs must be GUIDs.")
            .ValidateOnStart();
        services.AddOptions<AuthOptions>().Bind(configuration.GetSection("Authentication"))
            .Validate(o => o.HasValidOperatorOrigin(), "Authentication:OperatorBaseUrl must be an HTTPS origin without a path.")
            .Validate(o => new[] { o.ClientCertificatePath, o.DataProtectionCertificatePath, o.TokenCachePath,
                o.OwnerStatePath, o.DataProtectionKeysPath }.All(Path.IsPathFullyQualified), "Authentication file paths must be absolute.")
            .Validate(o => Path.GetFullPath(o.TokenCachePath) != Path.GetFullPath(o.OwnerStatePath), "Cache and owner paths must differ.")
            .ValidateOnStart();

        var auth = configuration.GetSection("Authentication").Get<AuthOptions>() ?? new();
        if (!auth.HasValidOperatorOrigin()) throw new InvalidOperationException("Configure Authentication:OperatorBaseUrl as an HTTPS origin.");
        var microsoft = configuration.GetSection("Microsoft").Get<MicrosoftOptions>() ?? new();
        var certificates = new AuthenticationCertificates(auth);
        services.AddSingleton(certificates);
        ProtectedFile.CreatePrivateDirectory(auth.DataProtectionKeysPath);
        services.AddDataProtection().SetApplicationName("GraphMcp")
            .PersistKeysToFileSystem(new DirectoryInfo(auth.DataProtectionKeysPath))
            .ProtectKeysWithCertificate(certificates.DataProtection);

        services.AddSingleton<OwnerIdentityStore>();
        services.AddSingleton<ProtectedFileTokenCache>();
        services.AddScoped<IGraphCredentialProvider, GraphCredentialProvider>();
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieScheme;
            options.DefaultChallengeScheme = OidcScheme;
        }).AddMicrosoftIdentityWebApp(options =>
        {
            options.Instance = "https://login.microsoftonline.com/";
            options.TenantId = microsoft.TenantId;
            options.ClientId = microsoft.ClientId;
            options.ClientCredentials = [CertificateDescription.FromCertificate(certificates.Client)];
            options.CallbackPath = AuthOptions.CallbackPath;
            options.SignedOutCallbackPath = "/operator/signed-out";
            options.MapInboundClaims = false;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.BackchannelTimeout = TimeSpan.FromSeconds(15);
            options.TokenValidationParameters.NameClaimType = "name";
            options.TokenValidationParameters.ValidateIssuer = true;
        }, cookies =>
        {
            cookies.Cookie.Name = "__Secure-GraphMcp.Operator";
            cookies.Cookie.Path = "/operator";
            cookies.Cookie.HttpOnly = true;
            cookies.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookies.Cookie.SameSite = SameSiteMode.Lax;
            cookies.ExpireTimeSpan = TimeSpan.FromHours(1);
            cookies.SlidingExpiration = false;
            cookies.Events.OnValidatePrincipal = context =>
            {
                var owner = context.HttpContext.RequestServices.GetRequiredService<OwnerIdentityStore>();
                if (!owner.Connected || !owner.IsExpectedOwner(context.Principal)
                    || context.Principal?.FindFirstValue(OwnerIdentityStore.GenerationClaim) != owner.Generation)
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
        }, openIdConnectScheme: OidcScheme, cookieScheme: CookieScheme)
            .EnableTokenAcquisitionToCallDownstreamApi(msal =>
            {
                msal.EnablePiiLogging = false;
                msal.RedirectUri = auth.CallbackUrl;
            }, GraphScopes.Required);

        services.Replace(ServiceDescriptor.Singleton<IMsalTokenCacheProvider>(p => p.GetRequiredService<ProtectedFileTokenCache>()));
        services.PostConfigure<OpenIdConnectOptions>(OidcScheme, options => ConfigureOidcEvents(options, auth, microsoft));
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "__Secure-GraphMcp.Antiforgery";
            options.Cookie.Path = "/operator";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.FormFieldName = "csrf";
        });
        return services;
    }

    private static void ConfigureOidcEvents(OpenIdConnectOptions options, AuthOptions auth, MicrosoftOptions microsoft)
    {
        options.CorrelationCookie.Path = "/operator";
        options.NonceCookie.Path = "/operator";
        options.UsePkce = true;
        options.ResponseType = "code";
        options.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "offline_access" }.Concat(GraphScopes.Required)) options.Scope.Add(scope);

        var redirect = options.Events.OnRedirectToIdentityProvider;
        options.Events.OnRedirectToIdentityProvider = async context =>
        {
            await redirect(context);
            context.ProtocolMessage.RedirectUri = auth.CallbackUrl;
        };

        // Runs before the MIW authorization-code handler can write to MSAL's cache.
        var codeReceived = options.Events.OnAuthorizationCodeReceived;
        options.Events.OnAuthorizationCodeReceived = async context =>
        {
            var owner = context.HttpContext.RequestServices.GetRequiredService<OwnerIdentityStore>();
            if (context.Properties is null
                || !context.Properties.Items.TryGetValue(OwnerIdentityStore.GenerationProperty, out var generation)
                || generation != owner.Generation)
            {
                context.Fail("This login attempt has expired. Start a new operator login.");
                return;
            }
            // MIW uses this redirect URI for code redemption. It must equal the public challenge URI.
            if (context.TokenEndpointRequest is not null) context.TokenEndpointRequest.RedirectUri = auth.CallbackUrl;
            await codeReceived(context);
        };

        var validated = options.Events.OnTokenValidated;
        options.Events.OnTokenValidated = async context =>
        {
            try { await validated(context); }
            catch (Exception)
            {
                OidcAcceptanceDiagnostics.Write(context.HttpContext, "token_validated", "preceding_handler", "failed");
                context.Fail("Microsoft login could not be accepted.");
                return;
            }
            if (context.Result is not null)
            {
                OidcAcceptanceDiagnostics.Write(context.HttpContext, "token_validated", "preceding_handler",
                    context.Result.Failure is null ? "stopped" : "failed");
                if (context.Result.Failure is not null) context.Fail("Microsoft login could not be accepted.");
                return;
            }
            var owner = context.HttpContext.RequestServices.GetRequiredService<OwnerIdentityStore>();
            if (!OidcAcceptanceDiagnostics.CheckOwner(context.HttpContext, owner, context.Principal, "token_validated"))
                context.Fail("Only the configured Microsoft account can connect.");
        };

        var ticket = options.Events.OnTicketReceived;
        options.Events.OnTicketReceived = async context =>
        {
            try { await ticket(context); }
            catch (Exception)
            {
                OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "preceding_handler", "failed");
                await RejectTicketAsync(context);
                return;
            }
            if (context.Result is not null)
            {
                OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "preceding_handler",
                    context.Result.Failure is null ? "stopped" : "failed");
                if (context.Result.Failure is not null) await RejectTicketAsync(context);
                return;
            }
            var owner = context.HttpContext.RequestServices.GetRequiredService<OwnerIdentityStore>();
            if (!OidcAcceptanceDiagnostics.CheckOwner(context.HttpContext, owner, context.Principal, "ticket_received"))
            {
                await RejectTicketAsync(context);
                return;
            }
            Microsoft.Identity.Client.AuthenticationResult result;
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "token_acquisition", "started");
            try
            {
                var acquisition = context.HttpContext.RequestServices.GetRequiredService<ITokenAcquisition>();
                result = await acquisition.GetAuthenticationResultForUserAsync(GraphScopes.Required,
                    authenticationScheme: OidcScheme, tenantId: microsoft.TenantId, user: context.Principal,
                    tokenAcquisitionOptions: new TokenAcquisitionOptions { CancellationToken = context.HttpContext.RequestAborted });
            }
            catch (Exception)
            {
                // Exception text/properties can contain credentials or identifiers. Record only this gate.
                OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "token_acquisition", "failed");
                await RejectTicketAsync(context);
                return;
            }
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "token_acquisition", "succeeded");
            var scopesApproved = GraphScopes.ContainsRequiredScopes(result.Scopes, out var hasAdditionalScopes);
            var homeAccountApproved = string.Equals(result.Account?.HomeAccountId.Identifier,
                microsoft.ExpectedHomeAccountId, StringComparison.OrdinalIgnoreCase);
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "graph_scopes", scopesApproved ? "approved" : "mismatch");
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "graph_additional_scopes", hasAdditionalScopes ? "present" : "absent");
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "home_account", homeAccountApproved ? "approved" : "mismatch");
            if (!scopesApproved || !homeAccountApproved)
            {
                await RejectTicketAsync(context);
                return;
            }
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "owner_persistence", "started");
            try { owner.Connect(context.Principal!); }
            catch (Exception)
            {
                OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "owner_persistence", "failed");
                await RejectTicketAsync(context);
                return;
            }
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "owner_persistence", "succeeded");
            ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(new Claim(OwnerIdentityStore.GenerationClaim, owner.Generation));
            context.ReturnUri = "/operator";
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "ticket_received", "acceptance", "succeeded");
        };

        options.Events.OnRemoteFailure = async context =>
        {
            OidcAcceptanceDiagnostics.Write(context.HttpContext, "remote_failure", "acceptance", "failed");
            context.HandleResponse();
            await WriteLoginFailureAsync(context.HttpContext);
        };
        options.Events.OnAuthenticationFailed = context =>
        {
            // OnRemoteFailure handles the safe response; raw protocol/MSAL exception details never become HTTP output.
            return Task.CompletedTask;
        };
    }

    private static Task RejectTicketAsync(TicketReceivedContext context)
    {
        // TicketReceived runs outside remote-failure handling. Fail() alone does not stop cookie sign-in.
        context.HandleResponse();
        return WriteLoginFailureAsync(context.HttpContext);
    }

    private static Task WriteLoginFailureAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync("Microsoft login failed. Start a new login from the operator page.");
    }

    // Call after the terminal MCP listener branch. Authentication must never run on MCP traffic.
    public static IApplicationBuilder UseGraphOperatorAuthentication(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path != AuthOptions.CallbackPath) { await next(context); return; }
            var owner = context.RequestServices.GetRequiredService<OwnerIdentityStore>();
            await owner.Gate.WaitAsync(context.RequestAborted);
            try { await next(context); }
            finally { owner.Gate.Release(); }
        });
        return app.UseAuthentication();
    }
}

internal sealed class AuthenticationCertificates : IDisposable
{
    public X509Certificate2 Client { get; }
    public X509Certificate2 DataProtection { get; }
    public AuthenticationCertificates(AuthOptions options)
    {
        Client = Load(options.ClientCertificatePath, options.ClientCertificatePasswordFile);
        try { DataProtection = Load(options.DataProtectionCertificatePath, options.DataProtectionCertificatePasswordFile); }
        catch { Client.Dispose(); throw; }
    }
    private static X509Certificate2 Load(string path, string? passwordFile)
    {
        var password = passwordFile is null ? null : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        // macOS does not support EphemeralKeySet; Linux containers keep keys exclusively in memory.
        var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        if (certificate.HasPrivateKey) return certificate;
        certificate.Dispose();
        throw new InvalidOperationException("Authentication certificates must contain private keys.");
    }
    public void Dispose() { Client.Dispose(); DataProtection.Dispose(); }
}
