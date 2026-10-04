using System.Net;
using System.Text.Json;
using GraphMcp.Infrastructure;
using GraphMcp.Models;

namespace GraphMcp.Tests.Graph;

public sealed class MailServiceTests
{
    [Fact]
    public async Task Utf32AttachmentIsNotMisinterpretedAsUtf16()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/$value", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xFE, 0, 0, 65, 0, 0, 0]) }
            : GraphTestFixture.Json("""{"@odata.type":"#microsoft.graph.fileAttachment","id":"a1","size":8,"contentType":"text/plain"}""")));
        var result = await fixture.Mail.GetAttachmentAsync(new() { MessageId = "m1", AttachmentId = "a1", Representation = "text" }, TestContext.Current.CancellationToken);
        Assert.Null(result.Content);
        Assert.Equal("unsupported_text_encoding", result.ContentUnavailableReason);
    }

    [Fact]
    public async Task DeeplyNestedHtmlUsesBoundedCallStack()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 5000)) + "Hello" + string.Concat(Enumerable.Repeat("</div>", 5000));
        var normalized = await TextNormalizer.NormalizeAsync(html, true, 100, TestContext.Current.CancellationToken);
        Assert.Equal("Hello", normalized.Text);
        Assert.False(normalized.Truncated);
    }

    [Theory]
    [InlineData(false, "utf-16le")]
    [InlineData(true, "utf-16be")]
    public async Task TextAttachmentsSupportBomMarkedUtf16(bool bigEndian, string encodingName)
    {
        var encoding = new System.Text.UnicodeEncoding(bigEndian, true, true);
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("Hello ☀")).ToArray();
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/$value", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : GraphTestFixture.Json("""{"@odata.type":"#microsoft.graph.fileAttachment","id":"a1","size":16,"contentType":"text/plain"}""")));
        var result = await fixture.Mail.GetAttachmentAsync(new() { MessageId = "m1", AttachmentId = "a1", Representation = "text" }, TestContext.Current.CancellationToken);
        Assert.Equal("Hello ☀", result.Content!.Text);
        Assert.Equal(encodingName, result.Content.Encoding);
    }

    [Fact]
    public async Task ListsOneBoundedPageAndDoesNotExposeGraphMetadata()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Contains("%24top=20", request.RequestUri!.Query);
            Assert.Contains("/me/mailFolders/inbox/messages", request.RequestUri.AbsolutePath);
            return Task.FromResult(GraphTestFixture.Json("""{"@odata.context":"secret-metadata","value":[{"id":"m1","subject":"Hello","sender":{"emailAddress":{"address":"sender@example.com"}},"bodyPreview":"Preview","isRead":false,"hasAttachments":true}]}"""));
        });
        var result = await fixture.Mail.ListAsync(new(), TestContext.Current.CancellationToken);
        Assert.Single(result.Items);
        Assert.Equal("m1", result.Items[0].Id);
        Assert.DoesNotContain("secret-metadata", JsonSerializer.Serialize(result));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task SearchUsesEncodedKqlAndNeverDownloadsBodies()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.DoesNotContain("$filter=", request.RequestUri!.Query);
            Assert.Contains("%24search=", request.RequestUri.Query);
            return Task.FromResult(GraphTestFixture.Json("""{"value":[{"id":"m1"}]}"""));
        });
        var result = await fixture.Mail.SearchAsync(new() { Query = "subject:budget & $filter=anything", Limit = 1 }, TestContext.Current.CancellationToken);
        Assert.True(result.PossiblyTruncated);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task RejectsInvalidPageSizesBeforeHttp(int pageSize)
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = pageSize }, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MessageNormalizesHtmlAndRetrievesInlineAttachmentMetadataOnly()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Contains("outlook.body-content-type=\"text\"", string.Join(",", request.Headers.GetValues("Prefer")));
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("attachments", StringComparison.Ordinal)
                ? GraphTestFixture.Json("""{"value":[{"@odata.type":"#microsoft.graph.fileAttachment","id":"a1","name":"inline.txt","size":4,"isInline":true,"contentBytes":"do-not-project"}]}""")
                : GraphTestFixture.Json("""{"id":"m1","hasAttachments":false,"body":{"contentType":"html","content":"<p>Hello &amp; goodbye</p><script>secret()</script><img src='https://invalid.test/pixel'>"}}"""));
        });
        var result = await fixture.Mail.GetAsync(new() { MessageId = "m1", MaxBodyChars = 8 }, TestContext.Current.CancellationToken);
        Assert.Equal("Hello &", result.BodyText);
        Assert.True(result.BodyTruncated);
        Assert.Single(result.Attachments);
        Assert.DoesNotContain("do-not-project", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("contentBytes", fixture.Handler.Requests[1].Query);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(2000000, "text/plain", "attachment_too_large")]
    [InlineData(100, "application/pdf", "binary_or_unsupported_content_type")]
    public async Task UnsupportedOrOversizedContentIsNotDownloaded(int size, string type, string reason)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.fileAttachment", ["id"] = "a1", ["size"] = size, ["contentType"] = type }))));
        var result = await fixture.Mail.GetAttachmentAsync(new() { MessageId = "m1", AttachmentId = "a1", Representation = "text" }, TestContext.Current.CancellationToken);
        Assert.Null(result.Content);
        Assert.Equal(reason, result.ContentUnavailableReason);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ExplicitTextAttachmentHasSeparateMetadataAndContent()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/$value", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello\r\nworld") }
            : GraphTestFixture.Json("""{"@odata.type":"#microsoft.graph.fileAttachment","id":"a1","size":12,"contentType":"text/plain"}""")));
        var result = await fixture.Mail.GetAttachmentAsync(new() { MessageId = "m1", AttachmentId = "a1", Representation = "text" }, TestContext.Current.CancellationToken);
        Assert.Equal("a1", result.Metadata.Id);
        Assert.Equal("hello\nworld", result.Content!.Text);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task MetadataIsDefaultAndBase64IsRejected()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("""{"id":"a1","size":12,"contentType":"text/plain"}""")));
        var result = await fixture.Mail.GetAttachmentAsync(new() { MessageId = "m1", AttachmentId = "a1" }, TestContext.Current.CancellationToken);
        Assert.Null(result.Content);
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.GetAttachmentAsync(new() { MessageId = "m1", AttachmentId = "a1", Representation = "base64" }, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Handler.Requests);
    }
}
