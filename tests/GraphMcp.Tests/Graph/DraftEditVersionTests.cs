using System.Text.Json.Nodes;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace GraphMcp.Tests.Graph;

public sealed class DraftEditVersionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("not-an-etag")]
    [InlineData("W/\"value\"\r\nHeader: injected")]
    public void MissingOrInvalidEtagsNeverCreateEditableVersions(string? etag)
    {
        var fixture = Create();
        Assert.Null(fixture.Versions.Issue("draft-id", etag, true));
    }

    [Fact]
    public void LiteralWeakEtagIsOpaqueAndBoundToCaseSensitiveMessageId()
    {
        var fixture = Create();
        var token = fixture.Versions.Issue("Draft-ID", "W/\"literal-version\"", true)!;
        Assert.DoesNotContain("literal-version", token);
        Assert.DoesNotContain("Draft-ID", token);
        Assert.Equal("W/\"literal-version\"", fixture.Versions.Read(token, "Draft-ID").LiteralETag);
        Assert.Throws<GraphOperationException>(() => fixture.Versions.Read(token, "draft-id"));
        Assert.Throws<GraphOperationException>(() => fixture.Versions.Read(token + "tampered", "Draft-ID"));
        Assert.Null(fixture.Versions.Issue("Draft-ID", "W/\"literal-version\"", false));
    }

    [Fact]
    public void ReconnectionInvalidatesVersionsAndCannotMintFromPreviousRead()
    {
        var fixture = Create();
        var generation = fixture.Credential.ConnectionGeneration;
        var token = fixture.Versions.Issue("draft-id", "\"etag\"", true)!;
        fixture.Credential.ConnectionGeneration = "reconnected";
        Assert.Throws<GraphOperationException>(() => fixture.Versions.Read(token, "draft-id"));
        Assert.Null(fixture.Versions.Issue("draft-id", "\"etag\"", true, generation));
    }

    [Fact]
    public void AccountAndTenantAndExpirationAreEnforced()
    {
        var fixture = Create();
        var token = fixture.Versions.Issue("draft-id", "\"etag\"", true)!;
        var otherOwner = new DraftEditVersionProtector(fixture.Protection, fixture.Credential, Options.Create(new MicrosoftOptions { TenantId = "tenant", ExpectedUserObjectId = "other" }));
        var otherTenant = new DraftEditVersionProtector(fixture.Protection, fixture.Credential, Options.Create(new MicrosoftOptions { TenantId = "other", ExpectedUserObjectId = "owner" }));
        Assert.Throws<GraphOperationException>(() => otherOwner.Read(token, "draft-id"));
        Assert.Throws<GraphOperationException>(() => otherTenant.Read(token, "draft-id"));
        var purpose = fixture.Protection.CreateProtector("GraphMcp.DraftEditVersion.v1");
        var state = JsonNode.Parse(purpose.Unprotect(token))!;
        state["expires"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expired = purpose.Protect(state.ToJsonString());
        Assert.Throws<GraphOperationException>(() => fixture.Versions.Read(expired, "draft-id"));
    }

    [Fact]
    public void CurrentEtagMustMatchLiterallyWithoutWeakTagSynthesis()
    {
        var fixture = Create();
        var version = fixture.Versions.Read(fixture.Versions.Issue("draft-id", "W/\"etag\"", true)!, "draft-id");
        fixture.Versions.EnsureCurrent(version, "W/\"etag\"");
        Assert.Throws<GraphOperationException>(() => fixture.Versions.EnsureCurrent(version, "\"etag\""));
        Assert.Throws<GraphOperationException>(() => fixture.Versions.EnsureCurrent(version, null));
    }

    private static (DraftEditVersionProtector Versions, FakeCredential Credential, IDataProtectionProvider Protection) Create()
    {
        var protection = new EphemeralDataProtectionProvider();
        var credential = new FakeCredential();
        return (new(protection, credential, Options.Create(new MicrosoftOptions { TenantId = "tenant", ExpectedUserObjectId = "owner" })), credential, protection);
    }
}
