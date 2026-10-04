using System.Net;
using System.Text.Json;
using GraphMcp.Configuration;
using GraphMcp.Graph;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace GraphMcp.Tests.Graph;

public sealed class ServiceRouteBoundaryTests
{
    private const string SummarySelect = "id,subject,sender,receivedDateTime,isRead,hasAttachments,bodyPreview,isDraft";
    private const string AttachmentSelect = "id,name,contentType,size,isInline,lastModifiedDateTime";
    private const string CalendarSelect = "id,name,isDefaultCalendar,owner";
    private const string EventSelect = "id,subject,start,end,isAllDay,originalStartTimeZone,originalEndTimeZone,location,organizer,attendees,isOnlineMeeting,onlineMeeting,isCancelled";
    private const string AccountJson = """{"id":"owner-id","mail":"owner@example.com","userPrincipalName":"owner@example.com"}""";
    private const string OwnedCalendar = """{"id":"calendar-id","owner":{"address":"owner@example.com"}}""";
    private const string Event = """{"id":"event-id","start":{"dateTime":"2026-10-04T10:00:00","timeZone":"UTC"},"end":{"dateTime":"2026-10-04T11:00:00","timeZone":"UTC"}}""";
    private const string Draft = """{"id":"draft-id","isDraft":true,"@odata.etag":"W/\"v1\""}""";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-04T10:00:00Z");
    private static readonly DateTimeOffset End = DateTimeOffset.Parse("2026-10-04T11:00:00Z");

    [Fact]
    public async Task InboxListProductionUrlPassesRealHttpAllowlist()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages?%24select=id%2Csubject%2Csender%2CreceivedDateTime%2CisRead%2ChasAttachments%2CbodyPreview%2CisDraft&%24top=5&%24orderby=receivedDateTime%20desc", request.RequestUri!.AbsoluteUri);
            AssertQuery(request.RequestUri, ("$select", SummarySelect), ("$top", "5"), ("$orderby", "receivedDateTime desc"));
            return Task.FromResult(GraphTestFixture.Json("""{"value":[]}"""));
        });

        await fixture.Mail.ListAsync(new() { Folder = "inbox", PageSize = 5 }, TestContext.Current.CancellationToken);

        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task InboxParenthesizedContinuationPassesOnlyOnExplicitNextPageRequest()
    {
        using var fixture = Fixture(Get("/v1.0/me/mailFolders/inbox/messages",
            """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/mailFolders('inbox')/messages?%24select=id%2Csubject%2Csender%2CreceivedDateTime%2CisRead%2ChasAttachments%2CbodyPreview%2CisDraft&%24top=5&%24orderby=receivedDateTime%20desc&%24skip=5"}""",
            ("$select", SummarySelect), ("$top", "5"), ("$orderby", "receivedDateTime desc")),
            Get("/v1.0/me/mailFolders('inbox')/messages", """{"value":[]}""",
                ("$select", SummarySelect), ("$top", "5"), ("$orderby", "receivedDateTime desc"), ("$skip", "5")));

        var request = new MailListRequest { Folder = "inbox", PageSize = 5 };
        var first = await fixture.Mail.ListAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(first.NextCursor);
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(fixture.Credentials.Refreshes);
        var second = await fixture.Mail.ListAsync(request with { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);

        Assert.Null(second.NextCursor);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal(2, fixture.Credentials.Refreshes.Count);
    }

    [Fact]
    public async Task AccountServiceQueryPassesRealHttpAllowlist()
    {
        using var fixture = Fixture(AccountRead());

        await Account(fixture).GetMeAsync(TestContext.Current.CancellationToken);

        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("all", "/v1.0/me/messages")]
    [InlineData("inbox", "/v1.0/me/mailFolders/inbox/messages")]
    [InlineData("sentitems", "/v1.0/me/mailFolders/sentitems/messages")]
    [InlineData("archive", "/v1.0/me/mailFolders/archive/messages")]
    [InlineData("drafts", "/v1.0/me/mailFolders/drafts/messages")]
    [InlineData("deleteditems", "/v1.0/me/mailFolders/deleteditems/messages")]
    [InlineData("junkemail", "/v1.0/me/mailFolders/junkemail/messages")]
    public async Task MailListQueriesIncludingDateFiltersPassRealHttpAllowlist(string folder, string path)
    {
        using var fixture = Fixture(Get(path, """{"value":[]}""",
            ("$select", SummarySelect), ("$top", "5"), ("$orderby", "receivedDateTime desc"),
            ("$filter", "receivedDateTime ge 2026-10-04T10:00:00.0000000Z and receivedDateTime lt 2026-10-04T11:00:00.0000000Z")));

        await fixture.Mail.ListAsync(new() { Folder = folder, PageSize = 5, ReceivedAfter = Start, ReceivedBefore = End }, TestContext.Current.CancellationToken);

        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MailSearchQueryPassesRealHttpAllowlist()
    {
        using var fixture = Fixture(Get("/v1.0/me/messages", """{"value":[]}""",
            ("$select", SummarySelect), ("$search", "\"subject:project AND from:sender@example.com\""), ("$top", "5")));

        await fixture.Mail.SearchAsync(new() { Query = "subject:project AND from:sender@example.com", Limit = 5 }, TestContext.Current.CancellationToken);

        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task MailGetAndAttachmentCollectionQueriesPassRealHttpAllowlist()
    {
        using var fixture = Fixture(
            Get("/v1.0/me/messages/AAMk%2Bmessage%2F%3D", """{"id":"AAMk+message/=","body":{"contentType":"text","content":"Message"}}""",
                ("$select", SummarySelect + ",from,toRecipients,ccRecipients,bccRecipients,sentDateTime,body")),
            Get("/v1.0/me/messages/AAMk%2Bmessage%2F%3D/attachments", """{"value":[]}""", ("$select", AttachmentSelect), ("$top", "100")));

        await fixture.Mail.GetAsync(new() { MessageId = "AAMk+message/=" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("text")]
    public async Task AttachmentMetadataAndContentRoutesPassRealHttpAllowlist(string representation)
    {
        const string path = "/v1.0/me/messages/AAMk%2Bmessage%2F%3D/attachments/AAMk%2Battachment%2F%3D";
        var metadata = Get(path, """{"id":"AAMk+attachment/=","@odata.type":"#microsoft.graph.fileAttachment","contentType":"text/plain","size":4}""",
            ("$select", AttachmentSelect));
        using var fixture = Fixture(representation == "metadata" ? [metadata] : [metadata, new(HttpMethod.Get, path + "/$value", "Text", [], IsText: true)]);

        var result = await fixture.Mail.GetAttachmentAsync(new() { MessageId = "AAMk+message/=", AttachmentId = "AAMk+attachment/=", Representation = representation }, TestContext.Current.CancellationToken);

        Assert.Equal(representation == "metadata" ? 1 : 2, fixture.Handler.Requests.Count);
        if (representation == "text") Assert.Equal("Text", result.Content!.Text);
    }

    [Fact]
    public async Task CalendarListQueryAndAccountOwnershipReadPassRealHttpAllowlist()
    {
        using var fixture = Fixture(AccountRead(), Get("/v1.0/me/calendars", """{"value":[]}""", ("$select", CalendarSelect), ("$top", "5")));

        await Calendar(fixture).ListAsync(new() { PageSize = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task CalendarListPaginationFollowsOnlyItsBoundQueryOnExplicitNextCall()
    {
        const string path = "/v1.0/me/calendars";
        (string, string)[] query = [("$select", CalendarSelect), ("$top", "1")];
        (string, string)[] nextQuery = [.. query, ("$skiptoken", "opaque+cursor/==")];
        var nextUrl = Url(path, nextQuery);
        using var fixture = Fixture(
            AccountRead(), Get(path, Page(OwnedCalendar, nextUrl), query),
            AccountRead(), Get(path, Page(OwnedCalendar.Replace("calendar-id", "next-calendar-id", StringComparison.Ordinal)), nextQuery));
        var service = Calendar(fixture);
        var request = new PageRequest { PageSize = 1 };

        var first = await service.ListAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("calendar-id", Assert.Single(first.Items).Id);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        var second = await service.ListAsync(request with { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);

        Assert.Equal("next-calendar-id", Assert.Single(second.Items).Id);
        Assert.Null(second.NextCursor);
        Assert.Equal(4, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("select")]
    [InlineData("page_size")]
    public async Task CalendarListRejectsContinuationForAnotherRouteOrQuery(string change)
    {
        var path = change == "route" ? "/v1.0/me/calendar/calendarView" : "/v1.0/me/calendars";
        var nextUrl = Url(path, ("$select", change == "select" ? "id,name" : CalendarSelect),
            ("$top", change == "page_size" ? "100" : "1"), ("$skiptoken", "opaque-token"));
        using var fixture = Fixture(AccountRead(), Get("/v1.0/me/calendars", Page(OwnedCalendar, nextUrl), ("$select", CalendarSelect), ("$top", "1")));

        var error = await Assert.ThrowsAsync<GraphOperationException>(() => Calendar(fixture).ListAsync(new() { PageSize = 1 }, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_upstream_response", error.Code);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, "/v1.0/me/calendar")]
    [InlineData("AAMk+calendar/=", "/v1.0/me/calendars/AAMk%2Bcalendar%2F%3D")]
    public async Task CalendarViewPaginationRechecksOwnershipAndFollowsBoundQuery(string? calendarId, string calendarPath)
    {
        var query = CalendarViewQuery();
        (string, string)[] nextQuery = [.. query, ("$skiptoken", "opaque+cursor/==")];
        var viewPath = calendarPath + "/calendarView";
        var firstView = Get(viewPath, Page(Event, Url(viewPath, nextQuery)), query);
        var secondView = Get(viewPath, Page(Event.Replace("event-id", "next-event-id", StringComparison.Ordinal)), nextQuery);
        using var fixture = Fixture(calendarId is null ? [firstView, secondView] : [
            AccountRead(), Get(calendarPath, OwnedCalendar, ("$select", CalendarSelect)), firstView,
            AccountRead(), Get(calendarPath, OwnedCalendar, ("$select", CalendarSelect)), secondView]);
        var service = Calendar(fixture);
        var request = new CalendarEventsRequest { CalendarId = calendarId, Start = Start, End = End, PageSize = 1 };

        var first = await service.GetEventsAsync(request, TestContext.Current.CancellationToken);
        Assert.NotNull(first.NextCursor);
        Assert.Equal("event-id", Assert.Single(first.Items).Id);
        Assert.Equal(calendarId is null ? 1 : 3, fixture.Handler.Requests.Count);
        var second = await service.GetEventsAsync(request with { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);

        Assert.Equal("next-event-id", Assert.Single(second.Items).Id);
        Assert.Null(second.NextCursor);
        Assert.Equal(calendarId is null ? 2 : 6, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, "calendar")]
    [InlineData(null, "start")]
    [InlineData(null, "end")]
    [InlineData(null, "select")]
    [InlineData(null, "page_size")]
    [InlineData("AAMk+calendar/=", "calendar")]
    [InlineData("AAMk+calendar/=", "start")]
    [InlineData("AAMk+calendar/=", "end")]
    [InlineData("AAMk+calendar/=", "select")]
    [InlineData("AAMk+calendar/=", "page_size")]
    public async Task CalendarViewRejectsContinuationForAnotherCalendarOrQuery(string? calendarId, string change)
    {
        var calendarPath = calendarId is null ? "/v1.0/me/calendar" : "/v1.0/me/calendars/AAMk%2Bcalendar%2F%3D";
        var viewPath = calendarPath + "/calendarView";
        var query = CalendarViewQuery();
        var nextQuery = query.ToDictionary(x => x.Name, x => x.Value);
        switch (change)
        {
            case "start": nextQuery["startDateTime"] = "2026-10-01T10:00:00.0000000Z"; break;
            case "end": nextQuery["endDateTime"] = "2026-12-04T11:00:00.0000000Z"; break;
            case "select": nextQuery["$select"] = "id,subject"; break;
            case "page_size": nextQuery["$top"] = "100"; break;
        }
        nextQuery["$skiptoken"] = "opaque-token";
        var nextPath = change == "calendar" ? "/v1.0/me/calendars/other-calendar/calendarView" : viewPath;
        var nextUrl = Url(nextPath, nextQuery.Select(x => (x.Key, x.Value)).ToArray());
        var view = Get(viewPath, Page(Event, nextUrl), query);
        using var fixture = Fixture(calendarId is null ? [view] : [AccountRead(), Get(calendarPath, OwnedCalendar, ("$select", CalendarSelect)), view]);

        var error = await Assert.ThrowsAsync<GraphOperationException>(() => Calendar(fixture).GetEventsAsync(
            new() { CalendarId = calendarId, Start = Start, End = End, PageSize = 1 }, TestContext.Current.CancellationToken));

        Assert.Equal("invalid_upstream_response", error.Code);
        Assert.Equal(calendarId is null ? 1 : 3, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, "/v1.0/me/calendar")]
    [InlineData("AAMk+calendar/=", "/v1.0/me/calendars/AAMk%2Bcalendar%2F%3D")]
    public async Task CalendarViewQueryAndOwnedCalendarPreflightPassRealHttpAllowlist(string? calendarId, string calendarPath)
    {
        var view = Get(calendarPath + "/calendarView", """{"value":[]}""",
            ("startDateTime", "2026-10-04T10:00:00.0000000Z"), ("endDateTime", "2026-10-04T11:00:00.0000000Z"),
            ("$select", EventSelect), ("$top", "5"));
        using var fixture = Fixture(calendarId is null ? [view] : [AccountRead(), Get(calendarPath, OwnedCalendar, ("$select", CalendarSelect)), view]);

        await Calendar(fixture).GetEventsAsync(new() { CalendarId = calendarId, Start = Start, End = End, PageSize = 5 }, TestContext.Current.CancellationToken);

        Assert.Equal(calendarId is null ? 1 : 3, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, "/v1.0/me/calendar")]
    [InlineData("AAMk+calendar/=", "/v1.0/me/calendars/AAMk%2Bcalendar%2F%3D")]
    public async Task CalendarEventQueryAndOwnedCalendarPreflightPassRealHttpAllowlist(string? calendarId, string calendarPath)
    {
        var item = Get(calendarPath + "/events/AAMk%2Bevent%2F%3D", Event, ("$select", EventSelect));
        using var fixture = Fixture(calendarId is null ? [item] : [AccountRead(), Get(calendarPath, OwnedCalendar, ("$select", CalendarSelect)), item]);

        await Calendar(fixture).GetEventAsync(new() { CalendarId = calendarId, EventId = "AAMk+event/=" }, TestContext.Current.CancellationToken);

        Assert.Equal(calendarId is null ? 1 : 3, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task AvailabilityServiceUsesOnlyApprovedSchedulePostAndAccountRead()
    {
        using var fixture = Fixture(AccountRead(), new(HttpMethod.Post, "/v1.0/me/calendar/getSchedule", """{"value":[]}""", []));

        await Calendar(fixture).GetAvailabilityAsync(new() { Start = Start, End = End }, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("new", "/v1.0/me/messages")]
    [InlineData("reply", "/v1.0/me/messages/AAMk%2Bsource%2F%3D/createReply")]
    [InlineData("replyAll", "/v1.0/me/messages/AAMk%2Bsource%2F%3D/createReplyAll")]
    [InlineData("forward", "/v1.0/me/messages/AAMk%2Bsource%2F%3D/createForward")]
    public async Task NativeDraftCreationServiceRoutesPassRealWriteAllowlist(string operation, string path)
    {
        using var fixture = Fixture(new ExpectedRequest(HttpMethod.Post, path, Draft, []));
        var service = new DraftService(fixture.Client, fixture.EditVersions, Options.Create(new DraftOptions()));
        var state = new DraftWriteState();
        var token = TestContext.Current.CancellationToken;

        var result = operation switch
        {
            "new" => await service.CreateAsync(new("Subject", "Body", [], [], []), state, token),
            "reply" => await service.CreateReplyAsync(new("AAMk+source/=", "Body"), state, token),
            "replyAll" => await service.CreateReplyAllAsync(new("AAMk+source/=", "Body"), state, token),
            _ => await service.CreateForwardAsync(new("AAMk+source/=", "Body", ["to@example.com"]), state, token)
        };

        Assert.Equal("draft-id", result.Id);
        Assert.Equal("committed", state.Outcome);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task DraftUpdatePreflightQueryAndConditionalPatchPassRealAllowlists()
    {
        using var fixture = Fixture(
            Get("/v1.0/me/messages/draft-id", Draft, ("$select", "id,isDraft")),
            new(HttpMethod.Patch, "/v1.0/me/messages/draft-id", Draft, [], IfMatch: "W/\"v1\""));
        var service = new DraftService(fixture.Client, fixture.EditVersions, Options.Create(new DraftOptions { EnableUpdates = true }));
        var version = fixture.EditVersions.Issue("draft-id", "W/\"v1\"", true)!;
        var state = new DraftWriteState();

        await service.UpdateAsync(new("draft-id", version, BodyText: "Updated"), state, TestContext.Current.CancellationToken);

        Assert.Equal("committed", state.Outcome);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    private sealed record ExpectedRequest(HttpMethod Method, string Path, string Response, (string Name, string Value)[] Query, bool IsText = false, string? IfMatch = null);

    private static ExpectedRequest Get(string path, string response, params (string Name, string Value)[] query) => new(HttpMethod.Get, path, response, query);
    private static ExpectedRequest AccountRead() => Get("/v1.0/me", AccountJson, ("$select", "id,displayName,mail,userPrincipalName"));
    private static AccountService Account(GraphTestFixture fixture) => new(fixture.Client, Options.Create(new MicrosoftOptions { ExpectedUserObjectId = "owner-id" }));
    private static CalendarService Calendar(GraphTestFixture fixture) => new(fixture.Client, Account(fixture), fixture.Cursors, Options.Create(fixture.Options));
    private static (string Name, string Value)[] CalendarViewQuery() => [
        ("startDateTime", "2026-10-04T10:00:00.0000000Z"), ("endDateTime", "2026-10-04T11:00:00.0000000Z"), ("$select", EventSelect), ("$top", "1")];
    private static string Url(string path, params (string Name, string Value)[] query) => QueryHelpers.AddQueryString(
        "https://graph.microsoft.com" + path, query.ToDictionary(x => x.Name, x => (string?)x.Value));
    private static string Page(string item, string? nextUrl = null)
    {
        using var document = JsonDocument.Parse(item);
        var page = new Dictionary<string, object> { ["value"] = new[] { document.RootElement.Clone() } };
        if (nextUrl is not null) page.Add("@odata.nextLink", nextUrl);
        return JsonSerializer.Serialize(page);
    }

    private static GraphTestFixture Fixture(params ExpectedRequest[] expected)
    {
        var index = 0;
        return new((request, _) =>
        {
            Assert.True(index < expected.Length, "The service issued an unexpected extra Graph request.");
            var next = expected[index++];
            Assert.Equal(next.Method, request.Method);
            Assert.Equal("https://graph.microsoft.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal(next.Path, request.RequestUri.AbsolutePath);
            AssertQuery(request.RequestUri, next.Query);
            if (next.IfMatch is not null) Assert.Equal(next.IfMatch, Assert.Single(request.Headers.GetValues("If-Match")));
            return Task.FromResult(next.IsText ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(next.Response) } : GraphTestFixture.Json(next.Response));
        });
    }

    private static void AssertQuery(Uri uri, params (string Name, string Value)[] expected)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(expected.Length, query.Count);
        foreach (var (name, value) in expected)
        {
            Assert.True(query.TryGetValue(name, out var actual), $"Expected query parameter {name}.");
            Assert.Equal(value, Assert.Single(actual));
        }
    }
}
