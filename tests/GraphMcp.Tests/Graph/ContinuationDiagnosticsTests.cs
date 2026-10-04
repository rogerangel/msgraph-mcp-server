using System.Text.Json;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Logging;

namespace GraphMcp.Tests.Graph;

public sealed class ContinuationDiagnosticsTests
{
    private const string PrivateFolderId = "Private-Folder-aAmK78b29060";
    private const string PrivateCalendarId = "private-calendar-0faabf8e";
    private const string PrivateUserId = "private-user-87bdc9fd";
    private const string PrivateMessageId = "private-message-65c45361";
    private const string PrivatePagingToken = "private-paging-token-63a059b1";
    private const string PrivateMailboxData = "private-mailbox-body-fa21bc7c";
    private const string PrivateQueryValue = "private-query-value-419314f2";
    private const string Origin = "https://graph.microsoft.com/v1.0/";

    [Theory]
    [InlineData("mail_key", "mail", "continuation_route", "me_mail_folder_key_predicate", "operation_not_allowed", "unsupported_key")]
    [InlineData("unicode_sentitems", "mail", "continuation_route", "me_mail_folder_key_predicate", "operation_not_allowed", "unsupported_key")]
    [InlineData("unicode_inbox", "mail", "continuation_route", "me_mail_folder_key_predicate", "operation_not_allowed", "unsupported_key")]
    [InlineData("fullwidth_inbox", "mail", "continuation_route", "me_mail_folder_key_predicate", "operation_not_allowed", "unsupported_key")]
    [InlineData("malformed_mail_key", "mail", "continuation_route", "me_mail_folder_key_predicate", "operation_not_allowed", "unsupported_key")]
    [InlineData("wrong_mail_key", "mail", "path_binding", "me_mail_folder_key_predicate", "invalid_upstream_response", "different_approved_alias")]
    [InlineData("mail_key_changed_top", "mail", "query_binding", "me_mail_folder_key_predicate", "invalid_upstream_response", "same_approved_alias")]
    [InlineData("mail_key_foreign_origin", "mail", "continuation_route", "me_mail_folder_key_predicate", "invalid_upstream_response", "same_approved_alias")]
    [InlineData("calendar_key", "calendar", "continuation_route", "me_calendar_key_predicate", "operation_not_allowed", "not_applicable")]
    [InlineData("user_key", "mail", "continuation_route", "user_key_predicate", "operation_not_allowed", "not_applicable")]
    [InlineData("user_segment", "mail", "continuation_route", "user_segment", "operation_not_allowed", "not_applicable")]
    [InlineData("wrong_calendar", "calendar", "path_binding", "me_calendar_segment", "invalid_upstream_response", "not_applicable")]
    [InlineData("changed_top", "mail", "query_binding", "me_mail_folder_segment", "invalid_upstream_response", "not_applicable")]
    [InlineData("extra_query", "mail", "query_binding", "me_mail_folder_segment", "invalid_upstream_response", "not_applicable")]
    public async Task Rejected_continuation_logs_only_fixed_diagnostics_after_one_successful_http_call(
        string shape, string service, string expectedStep, string expectedRouteShape, string expectedOutcome, string expectedFolderComparison)
    {
        var logger = new CapturingLogger();
        string? returnedNextLink = null;
        using var fixture = new GraphTestFixture((request, _) =>
        {
            // The first request has passed the real allowlist and received HTTP 200.
            // Only Microsoft's returned continuation is invalid, before any second call.
            returnedNextLink = NextLink(shape, request.RequestUri!);
            return Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["value"] = new[] { new { id = PrivateMessageId, bodyPreview = PrivateMailboxData } },
                ["@odata.nextLink"] = returnedNextLink
            })));
        }, cursorLogger: logger);

        var error = service == "mail"
            ? await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(
                new() { Folder = "inbox", PageSize = 5 }, TestContext.Current.CancellationToken))
            : await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Calendar.GetEventsAsync(
                new() { Start = DateTimeOffset.Parse("2026-10-01T00:00:00Z"), End = DateTimeOffset.Parse("2026-10-02T00:00:00Z"), PageSize = 5 },
                TestContext.Current.CancellationToken));

        Assert.Equal(expectedOutcome, error.Code);
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(fixture.Credentials.Refreshes);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(4200, entry.EventId.Id);
        Assert.Equal("GraphContinuationRejected", entry.EventId.Name);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal(new[] { "FolderKeyComparison", "Outcome", "RouteShape", "ValidationStep", "{OriginalFormat}" }, entry.Properties.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedStep, entry.Properties["ValidationStep"]);
        Assert.Equal(expectedRouteShape, entry.Properties["RouteShape"]);
        Assert.Equal(expectedOutcome, entry.Properties["Outcome"]);
        Assert.Equal(expectedFolderComparison, entry.Properties["FolderKeyComparison"]);

        AssertSafeLog(entry, returnedNextLink!, fixture.Handler.Requests[0].AbsoluteUri);
        Assert.DoesNotContain(returnedNextLink!, error.Message);
        Assert.DoesNotContain(PrivatePagingToken, error.Message);
    }

    [Theory]
    [InlineData("inbox")]
    [InlineData("InBoX")]
    public async Task Same_alias_continuation_logs_only_safe_acceptance_after_validation_and_preserves_the_returned_url(
        string alias)
    {
        var logger = new CapturingLogger();
        string? returnedNextLink = null;
        var calls = 0;
        using var fixture = new GraphTestFixture((request, _) =>
        {
            if (++calls == 1)
            {
                Assert.Equal("/v1.0/me/mailFolders/inbox/messages", request.RequestUri!.AbsolutePath);
                returnedNextLink = Origin + $"me/mailFolders('{alias}')/messages" + request.RequestUri.Query
                    + "&%24skiptoken=" + PrivatePagingToken + "%2B%2f%3D%3d%252F";
                return Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["value"] = new[] { new { id = PrivateMessageId, bodyPreview = PrivateMailboxData } },
                    ["@odata.nextLink"] = returnedNextLink
                })));
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(returnedNextLink, request.RequestUri!.OriginalString);
            return Task.FromResult(GraphTestFixture.Json("""{"value":[]}"""));
        }, cursorLogger: logger);

        var request = new MailListRequest { Folder = "inbox", PageSize = 5 };
        var first = await fixture.Mail.ListAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain(PrivatePagingToken, first.NextCursor);
        Assert.Single(fixture.Handler.Requests);
        Assert.Single(logger.Entries);

        var second = await fixture.Mail.ListAsync(request with { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);

        Assert.Null(second.NextCursor);
        Assert.Equal(2, fixture.Handler.Requests.Count);
        Assert.Equal(2, fixture.Credentials.Refreshes.Count);
        Assert.Equal(2, logger.Entries.Count);
        foreach (var entry in logger.Entries)
        {
            Assert.Equal(4201, entry.EventId.Id);
            Assert.Equal("GraphContinuationAccepted", entry.EventId.Name);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
            Assert.Equal(new[] { "FolderKeyComparison", "{OriginalFormat}" }, entry.Properties.Keys.Order(StringComparer.Ordinal).ToArray());
            Assert.Equal("same_approved_alias", entry.Properties["FolderKeyComparison"]);
            AssertSafeLog(entry, returnedNextLink!, fixture.Handler.Requests[0].AbsoluteUri, first.NextCursor, alias);
        }
    }

    [Fact]
    public async Task Valid_continuation_returns_opaque_cursor_without_rejection_diagnostics()
    {
        var logger = new CapturingLogger();
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(GraphTestFixture.Json(
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["value"] = Array.Empty<object>(),
                ["@odata.nextLink"] = request.RequestUri!.AbsoluteUri + "&%24skiptoken=" + PrivatePagingToken
            }))), cursorLogger: logger);

        var page = await fixture.Mail.ListAsync(new() { Folder = "inbox", PageSize = 5 }, TestContext.Current.CancellationToken);

        Assert.NotNull(page.NextCursor);
        Assert.DoesNotContain(PrivatePagingToken, page.NextCursor);
        Assert.Single(fixture.Handler.Requests);
        Assert.Empty(logger.Entries);
    }

    private static string NextLink(string shape, Uri initial)
    {
        var suffix = initial.Query + "&%24skiptoken=" + PrivatePagingToken;
        return shape switch
        {
            "mail_key" => Origin + $"me/mailFolders('{PrivateFolderId}')/messages" + suffix,
            "unicode_sentitems" => Origin + "me/mailFolders('ſentitems')/messages" + suffix,
            "unicode_inbox" => Origin + "me/mailFolders('ınbox')/messages" + suffix,
            "fullwidth_inbox" => Origin + "me/mailFolders('ｉnbox')/messages" + suffix,
            "malformed_mail_key" => Origin + $"me/mailFolders('{PrivateFolderId})/messages" + suffix,
            "wrong_mail_key" => Origin + "me/mailFolders('SentItems')/messages" + suffix,
            "mail_key_changed_top" => Origin + "me/mailFolders('inbox')/messages" + suffix.Replace("%24top=5", "%24top=6", StringComparison.Ordinal),
            "mail_key_foreign_origin" => "https://private-host.example/v1.0/me/mailFolders('inbox')/messages" + suffix,
            "calendar_key" => Origin + $"me/calendars('{PrivateCalendarId}')/calendarView" + suffix,
            "user_key" => Origin + $"users('{PrivateUserId}')/mailFolders/inbox/messages" + suffix,
            "user_segment" => Origin + $"users/{PrivateUserId}/mailFolders/inbox/messages" + suffix,
            "wrong_calendar" => Origin + $"me/calendars/{PrivateCalendarId}/calendarView" + suffix,
            "changed_top" => initial.AbsoluteUri.Replace("%24top=5", "%24top=6", StringComparison.Ordinal) + "&%24skiptoken=" + PrivatePagingToken,
            "extra_query" => initial.AbsoluteUri + "&privateFilter=" + PrivateQueryValue + "&%24skiptoken=" + PrivatePagingToken,
            _ => throw new InvalidOperationException()
        };
    }

    private static void AssertSafeLog(LogEntry entry, params string[] additionalSensitiveValues)
    {
        // Both formatted text and structured state must be safe for log sinks.
        var logged = entry.Message + JsonSerializer.Serialize(entry.Properties);
        foreach (var sensitive in new[]
        {
            Origin, PrivateFolderId, PrivateFolderId.ToLowerInvariant(), PrivateCalendarId, PrivateUserId, PrivateMessageId,
            PrivatePagingToken, PrivateMailboxData, PrivateQueryValue,
            "fake-token-never-log", "owner-id", "generation-a", "owner@example.com",
            "inbox", "InBoX", "SentItems", "sentitems", "private-host.example",
            "ſentitems", "ınbox", "ｉnbox", "%C5%BFentitems", "%C4%B1nbox", "%EF%BD%89nbox",
            "$skiptoken", "%24skiptoken", "$select", "startDateTime"
        }.Concat(additionalSensitiveValues))
            Assert.DoesNotContain(sensitive, logged);
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message,
        IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

    private sealed class CapturingLogger : ILogger<GraphCursorProtector>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary();
            Entries.Add(new(logLevel, eventId, formatter(state, exception), properties, exception));
        }
    }
}
