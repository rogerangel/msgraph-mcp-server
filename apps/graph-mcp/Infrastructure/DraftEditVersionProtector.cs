using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GraphMcp.Infrastructure;

/// <summary>Protects the exact Graph ETag and its account, connection, and message bindings.</summary>
public sealed class DraftEditVersionProtector(IDataProtectionProvider protection, IGraphCredentialProvider credentials, IOptions<MicrosoftOptions> microsoft)
{
    private readonly IDataProtector _protector = protection.CreateProtector("GraphMcp.DraftEditVersion.v1");
    private sealed record VersionState(string Tenant, string Owner, string Generation, string MessageId, string ETag, DateTimeOffset Expires);
    public sealed record EditVersion(string MessageId, string LiteralETag, string ConnectionGeneration);
    public string ConnectionGeneration => credentials.ConnectionGeneration;

    public string? Issue(string messageId, string? literalETag, bool isDraft, string? expectedGeneration = null)
    {
        if (!isDraft || !IsLiteralETag(literalETag)) return null;
        GraphInput.Id(messageId);
        var generation = credentials.ConnectionGeneration;
        if (expectedGeneration is not null && expectedGeneration != generation) return null;
        var state = new VersionState(microsoft.Value.TenantId, microsoft.Value.ExpectedUserObjectId, generation, messageId, literalETag!, DateTimeOffset.UtcNow.AddMinutes(30));
        var token = _protector.Protect(JsonSerializer.Serialize(state, GraphHttpClient.JsonOptions));
        return token.Length <= 16_384 ? token : null;
    }

    public EditVersion Read(string editVersion, string messageId)
    {
        GraphInput.Id(messageId);
        try
        {
            if (string.IsNullOrWhiteSpace(editVersion) || editVersion.Length > 16_384) throw new FormatException();
            var state = JsonSerializer.Deserialize<VersionState>(_protector.Unprotect(editVersion), GraphHttpClient.JsonOptions) ?? throw new FormatException();
            if (!string.Equals(state.Tenant, microsoft.Value.TenantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(state.Owner, microsoft.Value.ExpectedUserObjectId, StringComparison.OrdinalIgnoreCase)
                || state.Generation != credentials.ConnectionGeneration || state.MessageId != messageId
                || state.Expires <= DateTimeOffset.UtcNow || !IsLiteralETag(state.ETag)) throw new FormatException();
            return new(state.MessageId, state.ETag, state.Generation);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            throw new GraphOperationException("invalid_edit_version", "The edit version is invalid, expired, or belongs to another message or connection. Read the draft again.");
        }
    }

    public void EnsureCurrent(EditVersion version, string? currentLiteralETag)
    {
        if (version.ConnectionGeneration != credentials.ConnectionGeneration)
            throw new GraphOperationException("invalid_edit_version", "The Microsoft connection changed. Read the draft again.");
        if (!IsLiteralETag(currentLiteralETag) || !string.Equals(version.LiteralETag, currentLiteralETag, StringComparison.Ordinal))
            throw new GraphOperationException("draft_conflict", "The draft changed or its version cannot be verified. Read it again before editing.");
    }

    private static bool IsLiteralETag(string? value) => value is not null && value.Length is > 0 and <= 2048
        && !value.Any(char.IsControl) && EntityTagHeaderValue.TryParse(value, out var parsed)
        && parsed.Tag != "*" && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);
}
