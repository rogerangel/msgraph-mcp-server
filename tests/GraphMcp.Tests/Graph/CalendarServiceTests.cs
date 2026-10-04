using System.Text.Json;
using GraphMcp.Infrastructure;
using GraphMcp.Models;

namespace GraphMcp.Tests.Graph;

public sealed class CalendarServiceTests
{
    private const string EventJson = """{"id":"e1","subject":"Meeting","start":{"dateTime":"2026-11-01T06:30:00","timeZone":"UTC"},"end":{"dateTime":"2026-11-01T07:30:00","timeZone":"UTC"},"isAllDay":false,"originalStartTimeZone":"Eastern Standard Time","originalEndTimeZone":"Eastern Standard Time","isOnlineMeeting":true,"onlineMeeting":{"joinUrl":"https://teams.microsoft.com/example"}}""";

    [Fact]
    public async Task CalendarListExcludesSharedCalendars()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("""{"value":[{"id":"own","name":"My Calendar","owner":{"address":"OWNER@example.com"}},{"id":"other","name":"Shared","owner":{"address":"other@example.com"}}]}""")));
        var result = await fixture.Calendar.ListAsync(new(), TestContext.Current.CancellationToken);
        Assert.Single(result.Items);
        Assert.Equal("own", result.Items[0].Id);
    }

    [Fact]
    public async Task EventsUseCalendarViewAndConvertUtcAcrossDst()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.EndsWith("/me/calendar/calendarView", request.RequestUri!.AbsolutePath);
            Assert.Contains("startDateTime=", request.RequestUri.Query);
            return Task.FromResult(GraphTestFixture.Json("{\"value\":[" + EventJson + "]}"));
        });
        var result = await fixture.Calendar.GetEventsAsync(new() { Start = DateTimeOffset.Parse("2026-11-01T00:00:00-04:00"), End = DateTimeOffset.Parse("2026-11-02T00:00:00-05:00"), TimeZone = "America/New_York" }, TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromHours(-5), result.Items[0].Start.Offset);
        Assert.Equal(1, result.Items[0].Start.Hour);
        Assert.Equal("Eastern Standard Time", result.Items[0].OriginalStartTimeZone);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task SpecificCalendarIsCheckedBeforeReadingEvent()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/events/e1", StringComparison.Ordinal)
            ? GraphTestFixture.Json(EventJson)
            : GraphTestFixture.Json("""{"id":"c1","owner":{"address":"owner@example.com"}}""")));
        var result = await fixture.Calendar.GetEventAsync(new() { CalendarId = "c1", EventId = "e1" }, TestContext.Current.CancellationToken);
        Assert.Equal("e1", result.Id);
        Assert.Equal("/v1.0/me/calendars/c1", fixture.Handler.Requests[0].AbsolutePath);
        Assert.Equal("/v1.0/me/calendars/c1/events/e1", fixture.Handler.Requests[1].AbsolutePath);
    }

    [Fact]
    public async Task SharedCalendarCannotBeReadByGuessingItsId()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("""{"id":"c1","owner":{"address":"other@example.com"}}""")));
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Calendar.GetEventAsync(new() { CalendarId = "c1", EventId = "e1" }, TestContext.Current.CancellationToken));
        Assert.Equal("calendar_not_owned", error.Code);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(32)]
    public async Task InvalidEventRangesAreRejectedBeforeHttp(int days)
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException());
        var start = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Calendar.GetEventsAsync(new() { Start = start, End = start.AddDays(days) }, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task AvailabilityOnlyReturnsFreeBusyAndNeverMistakesErrorForFree()
    {
        using var fixture = new GraphTestFixture(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/me/calendar/getSchedule", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("owner@example.com", body.RootElement.GetProperty("schedules")[0].GetString());
            Assert.Equal("room@example.com", body.RootElement.GetProperty("schedules")[1].GetString());
            return GraphTestFixture.Json("""{"value":[{"scheduleId":"owner@example.com","availabilityView":"02","scheduleItems":[{"subject":"secret subject","location":"secret room"}]},{"scheduleId":"room@example.com","error":{"message":"confidential upstream error"}}]}""");
        });
        var result = await fixture.Calendar.GetAvailabilityAsync(new() { Start = DateTimeOffset.Parse("2026-10-01T10:00:00Z"), End = DateTimeOffset.Parse("2026-10-01T11:00:00Z"), AdditionalSchedules = ["room@example.com"] }, TestContext.Current.CancellationToken);
        Assert.Equal("02", result.Schedules[0].Slots);
        Assert.Null(result.Schedules[1].Slots);
        Assert.Equal("availability_unavailable", result.Schedules[1].Error);
        Assert.Equal("free_or_working_elsewhere", result.Legend["0"]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("confidential", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task AvailabilityRejectsLongWindowsAndUnexpectedExpansion()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("""{"value":[{"scheduleId":"owner@example.com","availabilityView":"00"},{"scheduleId":"unexpected@example.com","availabilityView":"00"}]}""")));
        var start = DateTimeOffset.Parse("2026-10-01T10:00:00Z");
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Calendar.GetAvailabilityAsync(new() { Start = start, End = start.AddDays(8) }, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Handler.Requests);
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Calendar.GetAvailabilityAsync(new() { Start = start, End = start.AddHours(1) }, TestContext.Current.CancellationToken));
    }
}
