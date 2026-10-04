using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using GraphMcp.Infrastructure;
using GraphMcp.Models;

namespace GraphMcp.Tests.Graph;

public sealed class MailContinuationCanonicalizationTests
{
    private const string Origin = "https://graph.microsoft.com";
    private const string Select = "id,subject,sender,receivedDateTime,isRead,hasAttachments,bodyPreview,isDraft";
    private const string OpaqueQuery = "?$skiptoken=opaque%2bpart%2F%3D%3d%252F%26%24top%3D999%23fragment&%24orderby=receivedDateTime+desc&$top=5&%24select=" + Select;

    [Theory]
    [InlineData("inbox", "inbox")]
    // System.Uri normalizes percent-encoded unreserved characters for path comparison;
    // the original continuation string and opaque query still travel unchanged.
    [InlineData("inbox", "in%62ox")]
    [InlineData("sentitems", "SentItems")]
    [InlineData("archive", "ARCHIVE")]
    [InlineData("drafts", "Drafts")]
    [InlineData("deleteditems", "DeletedItems")]
    [InlineData("junkemail", "JunkEmail")]
    public async Task KnownFolderContinuationIsBoundToOriginalFolderAndPreservedThroughCursorAndHttp(string folder, string key)
    {
        var nextLink = Origin + $"/v1.0/me/mailFolders('{key}')/messages" + OpaqueQuery;
        var calls = 0;
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            if (++calls == 1)
            {
                Assert.Equal($"/v1.0/me/mailFolders/{folder}/messages", request.RequestUri!.AbsolutePath);
                return Task.FromResult(Page("first", nextLink));
            }
            Assert.Equal(2, calls);
            Assert.Equal(nextLink, request.RequestUri!.OriginalString);
            Assert.Equal(OpaqueQuery, request.RequestUri.Query);
            return Task.FromResult(Page("second"));
        });
        var query = new MailListRequest { Folder = folder, PageSize = 5 };

        var first = await fixture.Mail.ListAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal("first", Assert.Single(first.Items).Id);
        Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain("opaque", first.NextCursor);
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(fixture.Credentials.Refreshes);
        var second = await fixture.Mail.ListAsync(query with { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);

        Assert.Equal("second", Assert.Single(second.Items).Id);
        Assert.Null(second.NextCursor);
        Assert.False(second.Truncated);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal(2, fixture.Credentials.Refreshes.Count);
    }

    [Fact]
    public async Task ThreePagesCanChangeOnlyKnownFolderSpellingWithoutReconstructingEitherPaginationToken()
    {
        var secondLink = Origin + "/v1.0/me/mailFolders('Inbox')/messages" + OpaqueQuery;
        var thirdLink = Origin + "/v1.0/me/mailFolders/inbox/messages?%24select=" + Select +
            "&$orderby=receivedDateTime%20desc&%24skiptoken=different%2F%2b%253d%26encoded%3Dvalue&$top=5";
        var calls = 0;
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            switch (++calls)
            {
                case 1: return Task.FromResult(Page("first", secondLink));
                case 2:
                    Assert.Equal(secondLink, request.RequestUri!.OriginalString);
                    return Task.FromResult(Page("second", thirdLink));
                case 3:
                    Assert.Equal(thirdLink, request.RequestUri!.OriginalString);
                    return Task.FromResult(Page("third"));
                default: throw new InvalidOperationException("Unexpected pagination request.");
            }
        });
        var query = new MailListRequest { Folder = "inbox", PageSize = 5 };

        var first = await fixture.Mail.ListAsync(query, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Handler.Requests);
        Assert.NotNull(first.NextCursor);
        var second = await fixture.Mail.ListAsync(query with { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.NotNull(second.NextCursor);
        var third = await fixture.Mail.ListAsync(query with { Cursor = second.NextCursor }, TestContext.Current.CancellationToken);

        Assert.Equal("third", Assert.Single(third.Items).Id);
        Assert.Null(third.NextCursor);
        Assert.Equal(3, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("sentitems")]
    [InlineData("archive")]
    [InlineData("drafts")]
    [InlineData("deleteditems")]
    [InlineData("junkemail")]
    public async Task KnownFolderKeyCannotChangeTheOriginalFolder(string otherFolder)
    {
        var nextLink = Origin + $"/v1.0/me/mailFolders('{otherFolder}')/messages" + OpaqueQuery;
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(Page("first", nextLink)));

        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(
            new() { Folder = "inbox", PageSize = 5 }, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_upstream_response", error.Code);
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(fixture.Credentials.Refreshes);
    }

    [Theory]
    [InlineData("select")]
    [InlineData("top")]
    [InlineData("orderby")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    public async Task CanonicalFolderContinuationStillRequiresTheOriginalBoundQuery(string change)
    {
        var query = change switch
        {
            "select" => OpaqueQuery.Replace(Select, "id,subject", StringComparison.Ordinal),
            "top" => OpaqueQuery.Replace("$top=5", "$top=100", StringComparison.Ordinal),
            "orderby" => OpaqueQuery.Replace("receivedDateTime+desc", "receivedDateTime+asc", StringComparison.Ordinal),
            "extra" => OpaqueQuery + "&$expand=attachments",
            "duplicate" => OpaqueQuery + "&%24skiptoken=second-token",
            _ => throw new InvalidOperationException()
        };
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(Page("first",
            Origin + "/v1.0/me/mailFolders('inbox')/messages" + query)));

        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(
            new() { Folder = "inbox", PageSize = 5 }, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_upstream_response", error.Code);
        Assert.Single(fixture.Handler.Requests);
    }

    public static TheoryData<string> RejectedUrls => new()
    {
        Origin + "/v1.0/me/mailFolders('custom-folder-id')/messages",
        Origin + "/v1.0/me/mailFolders('outbox')/messages",
        Origin + "/v1.0/me/mailFolders('all')/messages",
        Origin + "/v1.0/me/mailFolders('')/messages",
        Origin + "/v1.0/me/mailFolders(inbox)/messages",
        Origin + "/v1.0/me/mailFolders(\"inbox\")/messages",
        Origin + "/v1.0/me/mailFolders(id='inbox')/messages",
        Origin + "/v1.0/me/mailFolders('inbox','drafts')/messages",
        Origin + "/v1.0/me/mailFolders('inbox')/messages/extra",
        Origin + "/v1.0/me/mailFolders('inbox')/messages/",
        Origin + "/v1.0/me/mailFolders('inbox')/childFolders",
        Origin + "/v1.0/me/mailFolders('inbox')/messages/$count",
        Origin + "/v1.0/me/mailFolders(%27inbox%27)/messages",
        Origin + "/v1.0/me/mailFolders(%2527inbox%2527)/messages",
        Origin + "/v1.0/me/mailFolders('inbox%2Fdrafts')/messages",
        Origin + "/v1.0/me/mailFolders('inbox%27')/messages",
        Origin + "/v1.0/me/MailFolders('inbox')/messages",
        Origin + "/v1.0/Me/mailFolders('inbox')/messages",
        Origin + "/v1.0/me/mailFolders('inbox')/Messages",
        Origin + "/v1.0/me/calendars('calendar-id')/calendarView",
        Origin + "/v1.0/me/calendarView",
        Origin + "/v1.0/users('owner-id')/mailFolders('inbox')/messages",
        Origin + "/v1.0/users/owner-id/mailFolders('inbox')/messages",
        Origin + "/beta/me/mailFolders('inbox')/messages",
        "http://graph.microsoft.com/v1.0/me/mailFolders('inbox')/messages",
        "https://graph.microsoft.com:444/v1.0/me/mailFolders('inbox')/messages",
        "https://graph.microsoft.com.attacker.example/v1.0/me/mailFolders('inbox')/messages",
        "https://attacker.example/v1.0/me/mailFolders('inbox')/messages",
        "https://user@graph.microsoft.com/v1.0/me/mailFolders('inbox')/messages",
        Origin + "/v1.0/me/mailFolders('inbox')/messages#fragment"
    };

    [Theory]
    [MemberData(nameof(RejectedUrls))]
    public async Task CanonicalizationDoesNotAuthorizeUnknownKeysAliasesOrUntrustedOrigins(string url)
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException("HTTP must not be reached."));

        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync(url, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(fixture.Credentials.Refreshes);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("PUT")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void CanonicalizationDoesNotAuthorizeAnyAdditionalHttpMethod(string method)
    {
        const string url = Origin + "/v1.0/me/mailFolders('drafts')/messages";
        foreach (var validatorName in new[] { "ValidateUrl", "ValidateDraftUrl" })
        {
            var error = Assert.Throws<GraphOperationException>(() => Validate(validatorName, method, url));
            Assert.Equal("operation_not_allowed", error.Code);
        }
    }

    private static HttpResponseMessage Page(string id, string? nextLink = null)
    {
        var page = new Dictionary<string, object> { ["value"] = new[] { new { id } } };
        if (nextLink is not null) page.Add("@odata.nextLink", nextLink);
        return GraphTestFixture.Json(JsonSerializer.Serialize(page));
    }

    private static Uri Validate(string validatorName, string method, string url)
    {
        var validator = typeof(GraphHttpClient).GetMethod(validatorName, BindingFlags.Static | BindingFlags.NonPublic)!;
        try { return (Uri)validator.Invoke(null, [new HttpMethod(method), url])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
