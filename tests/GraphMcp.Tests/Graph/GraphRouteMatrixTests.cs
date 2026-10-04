using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using GraphMcp.Infrastructure;

namespace GraphMcp.Tests.Graph;

// Exercise the actual HTTP client's route gates: a successful case must reach the fake
// network, while denied cases must fail before either credentials or HTTP are used.
public sealed class GraphRouteMatrixTests
{
    private const string Origin = "https://graph.microsoft.com/v1.0/";

    public static TheoryData<string> ApprovedReadUrls => new()
    {
        "me?$select=id,displayName,mail,userPrincipalName",
        "me/messages?%24select=id%2Csubject%2Csender%2CreceivedDateTime%2CisRead%2ChasAttachments%2CbodyPreview%2CisDraft&%24top=5&%24orderby=receivedDateTime%20desc",
        "me/messages?%24select=id%2Csubject&%24search=%22subject%3Ainvoice%22&%24top=20",
        "me/mailFolders/inbox/messages?%24select=id%2Csubject%2Csender%2CreceivedDateTime%2CisRead%2ChasAttachments%2CbodyPreview%2CisDraft&%24top=5&%24orderby=receivedDateTime%20desc",
        "me/mailFolders/sentitems/messages?%24select=id%2Csubject&%24top=20&%24orderby=receivedDateTime%20desc",
        "me/mailFolders/archive/messages?%24select=id%2Csubject&%24top=20&%24orderby=receivedDateTime%20desc",
        "me/mailFolders/drafts/messages?%24select=id%2Csubject%2CisDraft&%24top=20&%24orderby=receivedDateTime%20desc",
        "me/mailFolders/deleteditems/messages?%24select=id%2Csubject&%24top=20&%24orderby=receivedDateTime%20desc",
        "me/mailFolders/junkemail/messages?%24select=id%2Csubject&%24top=20&%24orderby=receivedDateTime%20desc",
        "me/messages/message-123?%24select=id%2Csubject%2Cbody%2CtoRecipients%2CccRecipients%2CbccRecipients%2CisDraft",
        "me/messages/message-123?%24select=id%2CisDraft",
        "me/messages/message-123/attachments?%24select=id%2Cname%2CcontentType%2Csize%2CisInline%2ClastModifiedDateTime&%24top=100",
        "me/messages/message-123/attachments/attachment-123?%24select=id%2Cname%2CcontentType%2Csize%2CisInline%2ClastModifiedDateTime",
        "me/messages/message-123/attachments/attachment-123/$value",
        "me/calendars?%24select=id%2Cname%2CisDefaultCalendar%2Cowner&%24top=20",
        "me/calendars/calendar-123?%24select=id%2Cname%2CisDefaultCalendar%2Cowner",
        "me/calendar/calendarView?startDateTime=2026-10-01T00%3A00%3A00.0000000Z&endDateTime=2026-10-08T00%3A00%3A00.0000000Z&%24select=id%2Csubject%2Cstart%2Cend&%24top=20",
        "me/calendars/calendar-123/calendarView?startDateTime=2026-10-01T00%3A00%3A00.0000000Z&endDateTime=2026-10-08T00%3A00%3A00.0000000Z&%24select=id%2Csubject%2Cstart%2Cend&%24top=20",
        "me/calendar/events/event-123?%24select=id%2Csubject%2Cstart%2Cend%2Cattendees",
        "me/calendars/calendar-123/events/event-123?%24select=id%2Csubject%2Cstart%2Cend%2Cattendees",
        // Graph continuation links are absolute and retain the approved path and query.
        Origin + "me/mailFolders/inbox/messages?$select=id,subject&$top=5&$orderby=receivedDateTime%20desc&$skip=5",
        Origin + "me/calendars?$select=id,name,isDefaultCalendar,owner&$top=20&$skiptoken=next-page",
        // Opaque Graph IDs may contain escaped punctuation; they remain one path segment.
        "me/messages/AAMk%2B123%2F456%3D?%24select=id%2CisDraft"
    };

    [Theory]
    [MemberData(nameof(ApprovedReadUrls))]
    public async Task Approved_read_matrix_reaches_http_with_query_intact(string url)
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(GraphTestFixture.Json("{}"));
        });

        using var result = await fixture.Client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri(new Uri(Origin), url), Assert.Single(fixture.Handler.Requests));
        Assert.Single(fixture.Credentials.Refreshes);
    }

    [Fact]
    public async Task Attachment_value_uses_the_same_read_gate_on_the_byte_path()
    {
        const string url = "me/messages/message-123/attachments/attachment-123/$value";
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([65, 66]) });
        });
        Assert.Equal(new byte[] { 65, 66 }, await fixture.Client.GetAttachmentBytesAsync(url, TestContext.Current.CancellationToken));
        Assert.Equal(Origin + url, Assert.Single(fixture.Handler.Requests).AbsoluteUri);
    }

    [Fact]
    public async Task Schedule_post_is_the_only_post_allowed_by_the_read_gate()
    {
        using var fixture = new GraphTestFixture(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("availabilityViewInterval", await request.Content!.ReadAsStringAsync(cancellationToken));
            return GraphTestFixture.Json("{\"value\":[]}");
        });
        using var result = await fixture.Client.GetScheduleAsync(new
        {
            schedules = new[] { "owner@example.test" },
            startTime = new { dateTime = "2026-10-01T09:00:00", timeZone = "UTC" },
            endTime = new { dateTime = "2026-10-01T10:00:00", timeZone = "UTC" },
            availabilityViewInterval = 30
        }, TestContext.Current.CancellationToken);
        Assert.Equal(Origin + "me/calendar/getSchedule", Assert.Single(fixture.Handler.Requests).AbsoluteUri);
    }

    [Theory]
    [InlineData("create", "POST", "me/messages")]
    [InlineData("reply", "POST", "me/messages/message-123/createReply")]
    [InlineData("reply_all", "POST", "me/messages/message-123/createReplyAll")]
    [InlineData("forward", "POST", "me/messages/message-123/createForward")]
    [InlineData("update", "PATCH", "me/messages/message-123")]
    public async Task Fixed_draft_operations_reach_only_their_approved_routes(string operation, string method, string path)
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(method, request.Method.Method);
            if (operation == "update") Assert.Equal("W/\"version-1\"", Assert.Single(request.Headers.IfMatch).ToString());
            return Task.FromResult(GraphTestFixture.Json("{\"id\":\"draft-123\",\"isDraft\":true}"));
        });
        var state = new DraftWriteState();
        var body = new { subject = "Draft" };
        var token = TestContext.Current.CancellationToken;
        using var result = await (operation switch
        {
            "create" => fixture.Client.CreateDraftAsync(body, state, token),
            "reply" => fixture.Client.CreateReplyDraftAsync("message-123", body, state, token),
            "reply_all" => fixture.Client.CreateReplyAllDraftAsync("message-123", body, state, token),
            "forward" => fixture.Client.CreateForwardDraftAsync("message-123", body, state, token),
            "update" => fixture.Client.UpdateDraftAsync("message-123", body, "W/\"version-1\"", state, token),
            _ => throw new InvalidOperationException()
        });
        Assert.Equal(Origin + path, Assert.Single(fixture.Handler.Requests).AbsoluteUri);
        Assert.Equal("committed", state.Outcome);
    }

    public static TheoryData<string> RejectedReadUrls => new()
    {
        "me/messages/message-123/send?%24select=id",
        "me/messages/message-123/move?%24select=id",
        "me/messages/message-123/copy?%24select=id",
        "me/messages/message-123/createReply?%24select=id",
        "me/messages/message-123/attachments/attachment-123/$value/extra",
        "me/messages/message-123/attachments/attachment-123/contentBytes",
        "me/mailFolders/inbox/messages/message-123?%24select=id",
        "me/mailFolders/outbox/messages?%24top=5",
        "me/mailFolders/custom-folder/messages?%24top=5",
        "me/mailFolders/inbox/childFolders?%24top=5",
        "me/mailFolders/inbox/messages/extra/path?%24top=5",
        "me/calendar?%24select=id",
        "me/calendar/events?%24top=5",
        "me/calendars/calendar-123/events?%24top=5",
        "me/calendar/calendarView/extra?%24top=5",
        "me/calendar/events/event-123/instances?%24top=5",
        "me/calendar/getSchedule",
        "me/calendars/calendar-123/getSchedule",
        "me/calendarView?%24top=5",
        "me/events/event-123?%24select=id",
        "me/mailboxSettings?%24select=timeZone",
        "me/drive/root/children?%24top=5",
        "me/joinedTeams?%24top=5",
        "users/another-user/messages?%24top=5",
        "groups/group-123/calendar/events?%24top=5",
        "sites/root?%24select=id",
        "https://attacker.example/v1.0/me/messages?%24top=5",
        "https://graph.microsoft.com.attacker.example/v1.0/me/messages?%24top=5",
        "https://graph.microsoft.com/beta/me/messages?%24top=5",
        "http://graph.microsoft.com/v1.0/me/messages?%24top=5",
        "https://graph.microsoft.com:444/v1.0/me/messages?%24top=5",
        "https://user@graph.microsoft.com/v1.0/me/messages?%24top=5",
        "https://graph.microsoft.com/v1.0/me/messages?%24top=5#fragment"
    };

    [Theory]
    [MemberData(nameof(RejectedReadUrls))]
    public async Task Near_miss_and_arbitrary_read_urls_fail_before_credentials_or_http(string url)
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException("HTTP must not be reached"));
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync(url, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Credentials.Refreshes);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("POST", "me/messages")]
    [InlineData("PATCH", "me/messages/message-123")]
    [InlineData("DELETE", "me/messages/message-123")]
    [InlineData("PUT", "me/messages/message-123")]
    [InlineData("POST", "me/messages/message-123/createReply")]
    [InlineData("POST", "me/messages/message-123/send")]
    [InlineData("POST", "me/sendMail")]
    [InlineData("POST", "me/calendar/events")]
    [InlineData("PATCH", "me/calendar/events/event-123")]
    [InlineData("DELETE", "me/calendar/events/event-123")]
    [InlineData("HEAD", "me/messages?%24top=5")]
    [InlineData("POST", "me/calendar/getSchedule?%24select=availabilityView")]
    [InlineData("POST", "me/calendars/calendar-123/getSchedule")]
    [InlineData("POST", "users/another-user/calendar/getSchedule")]
    public void Read_validator_rejects_every_write_and_schedule_near_miss(string method, string url)
    {
        var error = Assert.Throws<GraphOperationException>(() => Validate("ValidateUrl", method, url));
        Assert.Equal("operation_not_allowed", error.Code);
    }

    [Theory]
    [InlineData("GET", "me/messages")]
    [InlineData("DELETE", "me/messages/message-123")]
    [InlineData("POST", "me/messages/message-123/send")]
    [InlineData("POST", "me/sendMail")]
    [InlineData("POST", "me/messages/message-123/move")]
    [InlineData("POST", "me/messages/message-123/copy")]
    [InlineData("POST", "me/messages/message-123/reply")]
    [InlineData("POST", "me/messages/message-123/replyAll")]
    [InlineData("POST", "me/messages/message-123/forward")]
    [InlineData("POST", "me/messages/message-123/createReply/extra")]
    [InlineData("POST", "me/messages/message-123/createReplyAll/extra")]
    [InlineData("POST", "me/messages/message-123/createForward/extra")]
    [InlineData("POST", "me/messages?%24select=id")]
    [InlineData("POST", "me/messages/message-123/createReply?%24select=id")]
    [InlineData("POST", "me/messages/message-123/createReplyAll?%24select=id")]
    [InlineData("POST", "me/messages/message-123/createForward?%24select=id")]
    [InlineData("PATCH", "me/messages/message-123?%24select=id")]
    [InlineData("PATCH", "me/messages/message-123/attachments/attachment-123")]
    [InlineData("PATCH", "me/messages")]
    [InlineData("POST", "me/mailFolders/drafts/messages")]
    [InlineData("POST", "users/another-user/messages")]
    [InlineData("PATCH", "users/another-user/messages/message-123")]
    [InlineData("POST", "me/calendar/getSchedule")]
    [InlineData("POST", "me/calendar/events")]
    [InlineData("PATCH", "me/calendar/events/event-123")]
    [InlineData("POST", "https://attacker.example/v1.0/me/messages")]
    [InlineData("POST", "https://graph.microsoft.com/beta/me/messages")]
    [InlineData("POST", "http://graph.microsoft.com/v1.0/me/messages")]
    [InlineData("POST", "https://graph.microsoft.com:444/v1.0/me/messages")]
    [InlineData("POST", "https://user@graph.microsoft.com/v1.0/me/messages")]
    [InlineData("POST", "me/messages#fragment")]
    public void Draft_validator_rejects_send_delete_other_mutations_and_query_bearing_routes(string method, string url)
    {
        var error = Assert.Throws<GraphOperationException>(() => Validate("ValidateDraftUrl", method, url));
        Assert.Equal("operation_not_allowed", error.Code);
    }

    private static Uri Validate(string validatorName, string method, string url)
    {
        // The validators stay non-public. Reflection tests forbidden method/path pairs
        // without adding a generic HTTP dispatch API to production just for tests.
        var validator = typeof(GraphHttpClient).GetMethod(validatorName, BindingFlags.Static | BindingFlags.NonPublic)!;
        try { return (Uri)validator.Invoke(null, [new HttpMethod(method), url])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}
