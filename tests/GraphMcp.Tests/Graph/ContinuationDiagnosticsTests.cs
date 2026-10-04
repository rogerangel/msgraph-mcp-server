using System.Text.Json;
using GraphMcp.Infrastructure;
using Microsoft.Extensions.Logging;

namespace GraphMcp.Tests.Graph;

public sealed class ContinuationDiagnosticsTests
{
    private const string PrivateFolderId = "private-folder-78b29060";
    private const string PrivateCalendarId = "private-calendar-0faabf8e";
    private const string PrivateUserId = "private-user-87bdc9fd";
    private const string PrivateMessageId = "private-message-65c45361";
    private const string PrivatePagingToken = "private-paging-token-63a059b1";
    private const string PrivateMailboxData = "private-mailbox-body-fa21bc7c";
    private const string PrivateQueryValue = "private-query-value-419314f2";
    private const string Origin = "https://graph.microsoft.com/v1.0/";

    [Theory]
    [InlineData("mail_key", "mail", "continuation_route", "me_mail_folder_key_predicate", "operation_not_allowed")]
    [InlineData("calendar_key", "calendar", "continuation_route", "me_calendar_key_predicate", "operation_not_allowed")]
    [InlineData("user_key", "mail", "continuation_route", "user_key_predicate", "operation_not_allowed")]
    [InlineData("user_segment", "mail", "continuation_route", "user_segment", "operation_not_allowed")]
    [InlineData("wrong_calendar", "calendar", "path_binding", "me_calendar_segment", "invalid_upstream_response")]
    [InlineData("changed_top", "mail", "query_binding", "me_mail_folder_segment", "invalid_upstream_response")]
    [InlineData("extra_query", "mail", "query_binding", "me_mail_folder_segment", "invalid_upstream_response")]
    public async Task Rejected_continuation_logs_only_fixed_diagnostics_after_one_successful_http_call(
        string shape, string service, string expectedStep, string expectedRouteShape, string expectedOutcome)
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
        Assert.Equal(new[] { "Outcome", "RouteShape", "ValidationStep", "{OriginalFormat}" }, entry.Properties.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(expectedStep, entry.Properties["ValidationStep"]);
        Assert.Equal(expectedRouteShape, entry.Properties["RouteShape"]);
        Assert.Equal(expectedOutcome, entry.Properties["Outcome"]);

        // Check both rendered log text and all structured values, rather than just the
        // message template: structured sinks must not receive hidden sensitive fields.
        var logged = entry.Message + JsonSerializer.Serialize(entry.Properties);
        foreach (var sensitive in new[]
        {
            returnedNextLink!, fixture.Handler.Requests[0].AbsoluteUri, Origin,
            PrivateFolderId, PrivateCalendarId, PrivateUserId, PrivateMessageId,
            PrivatePagingToken, PrivateMailboxData, PrivateQueryValue,
            "fake-token-never-log", "owner-id", "generation-a", "owner@example.com"
        })
            Assert.DoesNotContain(sensitive, logged);
        Assert.DoesNotContain("$skiptoken", logged);
        Assert.DoesNotContain("%24skiptoken", logged);
        Assert.DoesNotContain("$select", logged);
        Assert.DoesNotContain("startDateTime", logged);
        Assert.DoesNotContain(returnedNextLink!, error.Message);
        Assert.DoesNotContain(PrivatePagingToken, error.Message);
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
            "calendar_key" => Origin + $"me/calendars('{PrivateCalendarId}')/calendarView" + suffix,
            "user_key" => Origin + $"users('{PrivateUserId}')/mailFolders/inbox/messages" + suffix,
            "user_segment" => Origin + $"users/{PrivateUserId}/mailFolders/inbox/messages" + suffix,
            "wrong_calendar" => Origin + $"me/calendars/{PrivateCalendarId}/calendarView" + suffix,
            "changed_top" => initial.AbsoluteUri.Replace("%24top=5", "%24top=6", StringComparison.Ordinal) + "&%24skiptoken=" + PrivatePagingToken,
            "extra_query" => initial.AbsoluteUri + "&privateFilter=" + PrivateQueryValue + "&%24skiptoken=" + PrivatePagingToken,
            _ => throw new InvalidOperationException()
        };
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
