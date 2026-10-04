using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using GraphMcp.Configuration;

namespace GraphMcp.Auth;

public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/operator", (HttpContext context, OwnerIdentityStore owner, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // Browsers can apply form-action to the OIDC redirect after the local login POST.
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self' https://login.microsoftonline.com; frame-ancestors 'none'; base-uri 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            var token = WebUtility.HtmlEncode(antiforgery.GetAndStoreTokens(context).RequestToken);
            var status = owner.Connected ? "Microsoft account connected." : "Microsoft account not connected.";
            var logout = context.User.Identity?.IsAuthenticated == true
                ? $"<form action='/operator/logout' method='post'><input type='hidden' name='csrf' value='{token}'><button>Disconnect</button></form>" : "";
            return Results.Content($"<!doctype html><html lang='en'><meta charset='utf-8'><title>Graph MCP connection</title><h1>Graph MCP connection</h1><p>{status}</p><form action='/operator/login' method='post'><input type='hidden' name='csrf' value='{token}'><button>Connect or reauthenticate</button></form>{logout}</html>", "text/html");
        });

        endpoints.MapPost("/operator/login", async (HttpContext context, OwnerIdentityStore owner, IAntiforgery antiforgery) =>
        {
            if (!await ValidateAntiforgery(context, antiforgery)) return;
            await owner.Gate.WaitAsync(context.RequestAborted);
            try
            {
                var properties = new AuthenticationProperties { RedirectUri = "/operator" };
                properties.Items[OwnerIdentityStore.GenerationProperty] = owner.Generation;
                await context.ChallengeAsync(GraphAuthenticationExtensions.OidcScheme, properties);
            }
            finally { owner.Gate.Release(); }
        });

        endpoints.MapPost("/operator/logout", async (HttpContext context, OwnerIdentityStore owner,
            ProtectedFileTokenCache cache, IOptions<MicrosoftOptions> microsoft, IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated != true) { context.Response.StatusCode = 401; return; }
            if (!await ValidateAntiforgery(context, antiforgery)) return;
            await owner.Gate.WaitAsync(context.RequestAborted);
            try
            {
                // No token acquisition or callback can race this sequence. Next MSAL read clears its memory cache.
                try { owner.Disconnect(); }
                finally { await cache.ClearAsync(microsoft.Value.ExpectedHomeAccountId); }
                await context.SignOutAsync(GraphAuthenticationExtensions.CookieScheme);
                context.Response.Redirect("/operator");
            }
            finally { owner.Gate.Release(); }
        });
        return endpoints;
    }

    private static async Task<bool> ValidateAntiforgery(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { context.Response.StatusCode = 400; return false; }
    }
}
