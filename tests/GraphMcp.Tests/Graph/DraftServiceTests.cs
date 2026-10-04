using System.Net;
using System.Text.Json;
using GraphMcp.Configuration;
using GraphMcp.Graph;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Options;

namespace GraphMcp.Tests.Graph;

public sealed class DraftServiceTests
{
    private const string Draft = """{"id":"draft-id","isDraft":true,"@odata.etag":"W/\"v1\"","subject":"Draft","toRecipients":[{"emailAddress":{"address":"to@example.com"}}],"bccRecipients":[{"emailAddress":{"address":"bcc@example.com"}}],"bodyPreview":"preview"}""";

    [Fact]
    public async Task CreationIsAvailableWhenUpdatesAreDisabledAndUsesOnlyDraftFields()
    {
        using var fixture = new GraphTestFixture(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/me/messages", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new[] { "subject", "body", "toRecipients", "ccRecipients", "bccRecipients" }, body.RootElement.EnumerateObject().Select(x => x.Name));
            Assert.Equal("Text", body.RootElement.GetProperty("body").GetProperty("contentType").GetString());
            Assert.Equal("Text\nwith\ttabs", body.RootElement.GetProperty("body").GetProperty("content").GetString());
            Assert.Empty(body.RootElement.GetProperty("toRecipients").EnumerateArray());
            return GraphTestFixture.Json(Draft, HttpStatusCode.Created);
        });
        var service = Service(fixture);
        var state = new DraftWriteState();
        var result = await service.CreateAsync(new("Draft", "Text\nwith\ttabs", [], [], []), state, TestContext.Current.CancellationToken);
        Assert.True(result.IsDraft);
        Assert.NotNull(result.EditVersion);
        Assert.Equal("committed", state.Outcome);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("reply", "createReply")]
    [InlineData("replyAll", "createReplyAll")]
    [InlineData("forward", "createForward")]
    public async Task NativeDraftActionsDoNotSendOrIssueFollowupPatch(string operation, string action)
    {
        using var fixture = new GraphTestFixture(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/me/messages/source-id/" + action, request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("", body.RootElement.GetProperty("message").GetProperty("body").GetProperty("content").GetString());
            Assert.False(body.RootElement.TryGetProperty("comment", out _));
            return GraphTestFixture.Json(Draft, HttpStatusCode.Created);
        });
        var service = Service(fixture);
        var state = new DraftWriteState();
        var token = TestContext.Current.CancellationToken;
        var result = operation switch
        {
            "reply" => await service.CreateReplyAsync(new("source-id", ""), state, token),
            "replyAll" => await service.CreateReplyAllAsync(new("source-id", ""), state, token),
            _ => await service.CreateForwardAsync(new("source-id", "", ["to@example.com"]), state, token)
        };
        Assert.Equal("draft-id", result.Id);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ExplicitInputsAreBoundedBeforeHttp()
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException());
        var service = Service(fixture);
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<GraphOperationException>(() => service.CreateAsync(new(" ", "", [], [], []), new(), token));
        await Assert.ThrowsAsync<GraphOperationException>(() => service.CreateAsync(new("Draft", new string('x', 20_001), [], [], []), new(), token));
        await Assert.ThrowsAsync<GraphOperationException>(() => service.CreateAsync(new("Draft", "", Enumerable.Repeat("a@example.com", 11).ToArray(), Enumerable.Repeat("b@example.com", 10).ToArray(), []), new(), token));
        await Assert.ThrowsAsync<GraphOperationException>(() => service.CreateForwardAsync(new("source-id", "", []), new(), token));
        await Assert.ThrowsAsync<GraphOperationException>(() => service.CreateForwardAsync(new("source-id", "", ["Name <person@example.com>"]), new(), token));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ReplyAllResponseRecipientsAreBoundedWithoutRejectingNativeRecipients()
    {
        var recipients = Enumerable.Range(0, 120).Select(i => new { emailAddress = new { address = $"user{i}@example.com" } }).ToArray();
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new { id = "draft-id", isDraft = true, toRecipients = recipients }), HttpStatusCode.Created)));
        var result = await Service(fixture).CreateReplyAllAsync(new("source-id", "Reply"), new(), TestContext.Current.CancellationToken);
        Assert.Equal(100, result.To.Count);
        Assert.True(result.RecipientsTruncated);
        Assert.Null(result.EditVersion);
    }

    [Fact]
    public async Task UpdateGateStopsBeforeAnyGraphCall()
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException());
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => Service(fixture).UpdateAsync(new("draft-id", "unused", Subject: "Changed"), new(), TestContext.Current.CancellationToken));
        Assert.Equal("draft_updates_disabled", error.Code);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task AnyExistingDraftCanBeUpdatedWithItsExactVersionAndEmptyFieldsClear()
    {
        using var fixture = new GraphTestFixture(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get) return GraphTestFixture.Json(Draft);
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/v1.0/me/messages/draft-id", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"v1\"", Assert.Single(request.Headers.GetValues("If-Match")));
            using var patch = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new[] { "subject", "body", "ccRecipients" }, patch.RootElement.EnumerateObject().Select(x => x.Name));
            Assert.Equal("", patch.RootElement.GetProperty("subject").GetString());
            Assert.Equal("", patch.RootElement.GetProperty("body").GetProperty("content").GetString());
            Assert.Empty(patch.RootElement.GetProperty("ccRecipients").EnumerateArray());
            return GraphTestFixture.Json(Draft.Replace("v1", "v2", StringComparison.Ordinal));
        });
        var editVersion = fixture.EditVersions.Issue("draft-id", "W/\"v1\"", true)!;
        var state = new DraftWriteState();
        var result = await Service(fixture, enableUpdates: true).UpdateAsync(new("draft-id", editVersion, Subject: "", BodyText: "", CcRecipients: []), state, TestContext.Current.CancellationToken);
        Assert.NotEqual(editVersion, result.EditVersion);
        Assert.Equal("W/\"v2\"", fixture.EditVersions.Read(result.EditVersion!, "draft-id").LiteralETag);
        Assert.Equal("committed", state.Outcome);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task SuccessfulUpdateWithUnexpectedIdentityPreservesCommittedReceiptAndRequestedId()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(GraphTestFixture.Json(
            request.Method == HttpMethod.Get ? Draft : Draft.Replace("draft-id", "different-id", StringComparison.Ordinal))));
        var editVersion = fixture.EditVersions.Issue("draft-id", "W/\"v1\"", true)!;
        var state = new DraftWriteState();

        var error = await Assert.ThrowsAsync<GraphOperationException>(() => Service(fixture, true).UpdateAsync(
            new("draft-id", editVersion, Subject: "Changed"), state, TestContext.Current.CancellationToken));
        var receipt = state.ToReceipt(error: error);

        Assert.Equal("committed", receipt.Outcome);
        Assert.Equal("committed_details_unavailable", receipt.Code);
        Assert.Equal("draft-id", receipt.MessageId);
        Assert.False(receipt.RetrySafe);
        Assert.True(receipt.ReconciliationRequired);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, "W/\"v1\"", "not_a_draft")]
    [InlineData(true, "W/\"v2\"", "draft_conflict")]
    [InlineData(true, null, "draft_conflict")]
    public async Task UpdatePreflightRejectsNonDraftsAndChangedOrMissingVersions(bool isDraft, string? currentVersion, string code)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = "draft-id", ["isDraft"] = isDraft, ["@odata.etag"] = currentVersion }))));
        var editVersion = fixture.EditVersions.Issue("draft-id", "W/\"v1\"", true)!;
        var state = new DraftWriteState();
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => Service(fixture, true).UpdateAsync(new("draft-id", editVersion, BodyText: "Changed"), state, TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        Assert.Equal("not_applied", state.Outcome);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task InvalidEditVersionFailsBeforePreflight()
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<GraphOperationException>(() => Service(fixture, true).UpdateAsync(new("draft-id", "tampered", BodyText: "Changed"), new(), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MailReadExposesDraftIdentityBccAndVersionWithoutRawEtag()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(GraphTestFixture.Json(request.RequestUri!.AbsolutePath.EndsWith("/attachments", StringComparison.Ordinal) ? "{\"value\":[]}" : Draft)));
        var result = await fixture.Mail.GetAsync(new() { MessageId = "draft-id" }, TestContext.Current.CancellationToken);
        Assert.True(result.Summary.IsDraft);
        Assert.Equal("bcc@example.com", Assert.Single(result.Bcc!).Address);
        Assert.NotNull(result.EditVersion);
        Assert.DoesNotContain("@odata.etag", JsonSerializer.Serialize(result));
    }

    private static DraftService Service(GraphTestFixture fixture, bool enableUpdates = false) => new(fixture.Client, fixture.EditVersions, Options.Create(new DraftOptions { EnableUpdates = enableUpdates }));
}
