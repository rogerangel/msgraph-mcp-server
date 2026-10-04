using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace GraphMcp.Infrastructure;

public sealed class GraphCursorProtector(IDataProtectionProvider protection, IGraphCredentialProvider credentials,
    IOptions<MicrosoftOptions> microsoft, ILogger<GraphCursorProtector> logger)
{
    private readonly IDataProtector _protector = protection.CreateProtector("GraphMcp.Pagination.v1");
    internal sealed record PageContext(string InitialUrl, string CurrentUrl, string Binding, string Generation, DateTimeOffset Expires, int Page, int Seen);
    private sealed record CursorState(string InitialUrl, string NextUrl, string Binding, string Owner, string Generation, DateTimeOffset Expires, int Page, int Seen);

    internal PageContext Begin(string initialUrl, object arguments, string? cursor)
    {
        var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments, GraphHttpClient.JsonOptions))));
        if (cursor is null) return new(initialUrl, initialUrl, binding, credentials.ConnectionGeneration, DateTimeOffset.UtcNow.AddMinutes(30), 1, 0);
        try
        {
            if (cursor.Length > 16_384) throw new FormatException();
            var state = JsonSerializer.Deserialize<CursorState>(_protector.Unprotect(cursor), GraphHttpClient.JsonOptions) ?? throw new FormatException();
            if (state.Expires < DateTimeOffset.UtcNow || state.Owner != microsoft.Value.ExpectedUserObjectId || state.Generation != credentials.ConnectionGeneration || state.Binding != binding || state.InitialUrl != initialUrl || state.Page is < 2 or > 10 || state.Seen is < 0 or >= 1000)
                throw new FormatException();
            ValidateContinuation(initialUrl, state.NextUrl);
            return new(initialUrl, state.NextUrl, binding, state.Generation, state.Expires, state.Page, state.Seen);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or GraphOperationException or ArgumentException)
        {
            throw GraphOperationException.Invalid("The pagination cursor is invalid, expired, or belongs to a different query or connection.");
        }
    }

    internal (string? Cursor, bool Truncated) Next(PageContext context, string? nextLink, int returned)
    {
        if (context.Generation != credentials.ConnectionGeneration)
            throw GraphOperationException.Invalid("The Microsoft connection changed during this request. Start a new query.");
        if (nextLink is null) return (null, false);
        ValidateContinuation(context.InitialUrl, nextLink);
        if (context.Page >= 10 || context.Seen + returned >= 1000) return (null, true);
        var state = new CursorState(context.InitialUrl, nextLink, context.Binding, microsoft.Value.ExpectedUserObjectId, context.Generation, context.Expires, context.Page + 1, context.Seen + returned);
        var cursor = _protector.Protect(JsonSerializer.Serialize(state, GraphHttpClient.JsonOptions));
        if (cursor.Length > 16_384) throw new GraphOperationException("pagination_unavailable", "The pagination cursor exceeds the allowed size. Narrow the query.");
        return (cursor, false);
    }

    private void ValidateContinuation(string initialUrl, string nextUrl)
    {
        if (nextUrl.Length > 16_384) throw new GraphOperationException("pagination_unavailable", "The pagination cursor exceeds the allowed size. Narrow the query.");
        var step = "initial_route";
        try
        {
            var initial = GraphHttpClient.ValidateUrl(HttpMethod.Get, initialUrl);
            step = "continuation_route";
            var next = GraphHttpClient.ValidateUrl(HttpMethod.Get, nextUrl);
            step = "path_binding";
            if (initial.AbsolutePath != next.AbsolutePath) throw new GraphOperationException("invalid_upstream_response", "An invalid pagination route was rejected.");
            step = "query_binding";
            var expected = QueryHelpers.ParseQuery(initial.Query);
            var actual = QueryHelpers.ParseQuery(next.Query);
            foreach (var pair in expected)
                if (!actual.TryGetValue(pair.Key, out var value) || pair.Value.Count != 1 || value.Count != 1 || pair.Value[0] != value[0])
                    throw new GraphOperationException("invalid_upstream_response", "An invalid pagination query was rejected.");
            foreach (var pair in actual)
                if ((!expected.ContainsKey(pair.Key) && !pair.Key.Equals("$skip", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("$skiptoken", StringComparison.OrdinalIgnoreCase)) || pair.Value.Count != 1)
                    throw new GraphOperationException("invalid_upstream_response", "An invalid pagination query was rejected.");
        }
        catch (GraphOperationException error)
        {
            // Only fixed labels: a continuation can contain private IDs and opaque paging tokens.
            logger.LogWarning(new EventId(4200, "GraphContinuationRejected"),
                "Graph continuation rejected at {ValidationStep}; route shape {RouteShape}; outcome {Outcome}",
                step, ContinuationRouteShape(nextUrl), error.Code);
            throw;
        }
    }

    private static string ContinuationRouteShape(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "relative_or_invalid";
        var path = uri.AbsolutePath;
        // These classifications diagnose spelling only; they never authorize or rewrite a route.
        if (path.StartsWith("/v1.0/me/mailFolders(", StringComparison.OrdinalIgnoreCase)) return "me_mail_folder_key_predicate";
        if (path.StartsWith("/v1.0/me/mailFolders/", StringComparison.OrdinalIgnoreCase)) return "me_mail_folder_segment";
        if (path.StartsWith("/v1.0/me/calendars(", StringComparison.OrdinalIgnoreCase)) return "me_calendar_key_predicate";
        if (path.StartsWith("/v1.0/me/calendars/", StringComparison.OrdinalIgnoreCase)) return "me_calendar_segment";
        if (path.Equals("/v1.0/me/calendars", StringComparison.OrdinalIgnoreCase)) return "me_calendars";
        if (path.StartsWith("/v1.0/me/calendar/", StringComparison.OrdinalIgnoreCase)) return "me_default_calendar";
        if (path.Equals("/v1.0/me/calendarView", StringComparison.OrdinalIgnoreCase)) return "me_calendar_view_alias";
        if (path.StartsWith("/v1.0/users(", StringComparison.OrdinalIgnoreCase)) return "user_key_predicate";
        if (path.StartsWith("/v1.0/users/", StringComparison.OrdinalIgnoreCase)) return "user_segment";
        if (path.Equals("/v1.0/me/messages", StringComparison.OrdinalIgnoreCase)) return "me_messages";
        return "other";
    }
}
