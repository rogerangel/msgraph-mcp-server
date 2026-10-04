using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace GraphMcp.Infrastructure;

public sealed class GraphCursorProtector(IDataProtectionProvider protection, IGraphCredentialProvider credentials, IOptions<MicrosoftOptions> microsoft)
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

    private static void ValidateContinuation(string initialUrl, string nextUrl)
    {
        if (nextUrl.Length > 16_384) throw new GraphOperationException("pagination_unavailable", "The pagination cursor exceeds the allowed size. Narrow the query.");
        var initial = GraphHttpClient.ValidateUrl(HttpMethod.Get, initialUrl);
        var next = GraphHttpClient.ValidateUrl(HttpMethod.Get, nextUrl);
        if (initial.AbsolutePath != next.AbsolutePath) throw new GraphOperationException("invalid_upstream_response", "An invalid pagination route was rejected.");
        var expected = QueryHelpers.ParseQuery(initial.Query);
        var actual = QueryHelpers.ParseQuery(next.Query);
        foreach (var pair in expected)
            if (!actual.TryGetValue(pair.Key, out var value) || pair.Value.Count != 1 || value.Count != 1 || pair.Value[0] != value[0])
                throw new GraphOperationException("invalid_upstream_response", "An invalid pagination query was rejected.");
        foreach (var pair in actual)
            if ((!expected.ContainsKey(pair.Key) && !pair.Key.Equals("$skip", StringComparison.OrdinalIgnoreCase) && !pair.Key.Equals("$skiptoken", StringComparison.OrdinalIgnoreCase)) || pair.Value.Count != 1)
                throw new GraphOperationException("invalid_upstream_response", "An invalid pagination query was rejected.");
    }
}
