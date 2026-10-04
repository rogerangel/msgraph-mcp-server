using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Options;

namespace GraphMcp.Graph;

public sealed class CalendarService(GraphHttpClient graph, IAccountService accounts, GraphCursorProtector cursors, IOptions<GraphOptions> options) : ICalendarService
{
    private readonly GraphOptions _options = options.Value;
    private const string CalendarSelect = "id,name,isDefaultCalendar,owner";
    private const string EventSelect = "id,subject,start,end,isAllDay,originalStartTimeZone,originalEndTimeZone,location,organizer,attendees,isOnlineMeeting,onlineMeeting,isCancelled";

    public async Task<PageResult<CalendarDto>> ListAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var size = GraphInput.PageSize(request.PageSize, _options.MaxPageSize);
        var url = GraphInput.Query("me/calendars", ("$select", CalendarSelect), ("$top", size.ToString(CultureInfo.InvariantCulture)));
        var page = cursors.Begin(url, request with { Cursor = null }, request.Cursor);
        var account = await accounts.GetMeAsync(cancellationToken);
        using var response = await graph.GetAsync(page.CurrentUrl, cancellationToken);
        var values = response.RootElement.Array("value").ToArray();
        if (values.Length > size) throw InvalidResponse();
        var next = cursors.Next(page, response.RootElement.Text("@odata.nextLink"), values.Length);
        var calendars = values.Where(x => IsOwner(x, account)).Select(x => new CalendarDto(x.Text("id") ?? "", x.BoundedText("name", 256), x.Bool("isDefaultCalendar"))).ToArray();
        return new(calendars, next.Cursor, next.Truncated);
    }

    public async Task<PageResult<CalendarEventDto>> GetEventsAsync(CalendarEventsRequest request, CancellationToken cancellationToken)
    {
        GraphInput.Range(request.Start, request.End, _options.MaxCalendarRangeDays);
        var zone = GraphInput.Zone(request.TimeZone);
        var size = GraphInput.PageSize(request.PageSize, _options.MaxPageSize);
        var path = CalendarPath(request.CalendarId);
        var url = GraphInput.Query(path + "/calendarView", ("startDateTime", GraphInput.Date(request.Start)), ("endDateTime", GraphInput.Date(request.End)), ("$select", EventSelect), ("$top", size.ToString(CultureInfo.InvariantCulture)));
        var page = cursors.Begin(url, request with { Cursor = null }, request.Cursor);
        await CheckOwnerAsync(path, cancellationToken);
        using var response = await graph.GetAsync(page.CurrentUrl, cancellationToken);
        var values = response.RootElement.Array("value").ToArray();
        if (values.Length > size) throw InvalidResponse();
        var next = cursors.Next(page, response.RootElement.Text("@odata.nextLink"), values.Length);
        return new(values.Select(x => MapEvent(x, zone)).ToArray(), next.Cursor, next.Truncated);
    }

    public async Task<CalendarEventDto> GetEventAsync(CalendarEventRequest request, CancellationToken cancellationToken)
    {
        var eventId = GraphInput.Id(request.EventId);
        var zone = GraphInput.Zone(request.TimeZone);
        var path = CalendarPath(request.CalendarId);
        await CheckOwnerAsync(path, cancellationToken);
        using var response = await graph.GetAsync(GraphInput.Query(path + "/events/" + eventId, ("$select", EventSelect)), cancellationToken);
        return MapEvent(response.RootElement, zone);
    }

    public async Task<AvailabilityResult> GetAvailabilityAsync(AvailabilityRequest request, CancellationToken cancellationToken)
    {
        GraphInput.Range(request.Start, request.End, 7);
        var zone = GraphInput.Zone(request.TimeZone);
        if (request.IntervalMinutes is not (15 or 30 or 60)) throw GraphOperationException.Invalid("intervalMinutes must be 15, 30, or 60.");
        if (request.AdditionalSchedules is null || request.AdditionalSchedules.Length > 9) throw GraphOperationException.Invalid("At most nine additional email addresses are permitted.");
        foreach (var address in request.AdditionalSchedules) ValidateEmail(address);
        var account = await accounts.GetMeAsync(cancellationToken);
        var ownAddress = account.Mail ?? account.UserPrincipalName;
        if (ownAddress is null) throw new GraphOperationException("mailbox_unavailable", "The connected account has no usable mailbox address.");
        ValidateEmail(ownAddress);
        var addresses = new[] { ownAddress }.Concat(request.AdditionalSchedules).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var body = new
        {
            schedules = addresses,
            startTime = new { dateTime = request.Start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            endTime = new { dateTime = request.End.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            availabilityViewInterval = request.IntervalMinutes
        };
        using var response = await graph.GetScheduleAsync(body, cancellationToken);
        var values = response.RootElement.Array("value").ToArray();
        if (values.Length > addresses.Length) throw InvalidResponse();
        var results = new Dictionary<string, ScheduleAvailabilityDto>(StringComparer.OrdinalIgnoreCase);
        var expectedSlots = (int)Math.Ceiling((request.End - request.Start).TotalMinutes / request.IntervalMinutes);
        foreach (var value in values)
        {
            var address = value.Text("scheduleId");
            if (address is null || !addresses.Contains(address, StringComparer.OrdinalIgnoreCase) || results.ContainsKey(address)) throw InvalidResponse();
            var slots = value.Text("availabilityView");
            var hasError = value.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
            var valid = !hasError && slots is not null && slots.Length == expectedSlots && slots.All(x => x is '0' or '1' or '2' or '3');
            results.Add(address, new(address, valid ? slots : null, valid ? null : "availability_unavailable"));
        }
        var output = addresses.Select(address => results.GetValueOrDefault(address) ?? new(address, null, "availability_unavailable")).ToArray();
        return new(TimeZoneInfo.ConvertTime(request.Start, zone), TimeZoneInfo.ConvertTime(request.End, zone), zone.Id, request.IntervalMinutes,
            new Dictionary<string, string> { ["0"] = "free_or_working_elsewhere", ["1"] = "tentative", ["2"] = "busy", ["3"] = "out_of_office" }, output);
    }

    private async Task CheckOwnerAsync(string calendarPath, CancellationToken cancellationToken)
    {
        // The default /me/calendar is necessarily the connected user's calendar.
        if (calendarPath == "me/calendar") return;
        var account = await accounts.GetMeAsync(cancellationToken);
        using var calendar = await graph.GetAsync(GraphInput.Query(calendarPath, ("$select", CalendarSelect)), cancellationToken);
        if (!IsOwner(calendar.RootElement, account)) throw new GraphOperationException("calendar_not_owned", "Only calendars owned by the connected account can be read.");
    }

    private static bool IsOwner(JsonElement calendar, AccountDto account)
    {
        var owner = calendar.Object("owner").Text("address");
        return owner is not null && (owner.Equals(account.Mail, StringComparison.OrdinalIgnoreCase) || owner.Equals(account.UserPrincipalName, StringComparison.OrdinalIgnoreCase));
    }
    private static string CalendarPath(string? calendarId) => calendarId is null ? "me/calendar" : "me/calendars/" + GraphInput.Id(calendarId);
    private static void ValidateEmail(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 254 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || !address.Contains('@') || address.Any(char.IsControl)) throw GraphOperationException.Invalid("Schedules must be individual SMTP email addresses without display names.");
    }
    private static CalendarEventDto MapEvent(JsonElement value, TimeZoneInfo zone)
    {
        var attendees = value.Array("attendees").ToArray();
        var projected = attendees.Take(100).Select(item => new AttendeeDto(item.Object("emailAddress").BoundedText("name", 256), item.Object("emailAddress").BoundedText("address", 320), item.BoundedText("type", 32), item.Object("status").BoundedText("response", 32))).ToArray();
        var meetingUrl = value.Object("onlineMeeting").Text("joinUrl");
        if (meetingUrl?.Length > 2048 || !Uri.TryCreate(meetingUrl, UriKind.Absolute, out var link) || link.Scheme != "https") meetingUrl = null;
        return new(value.Text("id") ?? "", value.BoundedText("subject", 512), EventTime(value.Object("start"), zone), EventTime(value.Object("end"), zone), zone.Id, value.Bool("isAllDay"), value.BoundedText("originalStartTimeZone", 128), value.BoundedText("originalEndTimeZone", 128), value.Object("location").BoundedText("displayName", 512), MailService.Address(value.Object("organizer")), projected, attendees.Length > 100, value.Bool("isOnlineMeeting"), meetingUrl, value.Bool("isCancelled"));
    }
    private static DateTimeOffset EventTime(JsonElement value, TimeZoneInfo zone)
    {
        if (value.Text("timeZone") != "UTC" || !DateTimeOffset.TryParse(value.Text("dateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) throw InvalidResponse();
        return TimeZoneInfo.ConvertTime(utc, zone);
    }
    private static GraphOperationException InvalidResponse() => new("invalid_upstream_response", "Microsoft Graph returned an invalid or unexpectedly broad calendar response.");
}
