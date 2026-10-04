namespace GraphMcp.Models;

public record PageRequest { public int PageSize { get; init; } = 20; public string? Cursor { get; init; } }
public sealed record MailListRequest : PageRequest
{
    public string Folder { get; init; } = "inbox";
    public DateTimeOffset? ReceivedAfter { get; init; }
    public DateTimeOffset? ReceivedBefore { get; init; }
}
public sealed record MailSearchRequest { public string Query { get; init; } = ""; public int Limit { get; init; } = 20; }
public sealed record MailGetRequest { public string MessageId { get; init; } = ""; public int MaxBodyChars { get; init; } = 20_000; }
public sealed record AttachmentRequest
{
    public string MessageId { get; init; } = "";
    public string AttachmentId { get; init; } = "";
    public string Representation { get; init; } = "metadata";
}
public sealed record CalendarEventsRequest : PageRequest
{
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string? CalendarId { get; init; }
    public string TimeZone { get; init; } = "UTC";
}
public sealed record CalendarEventRequest
{
    public string EventId { get; init; } = "";
    public string? CalendarId { get; init; }
    public string TimeZone { get; init; } = "UTC";
}
public sealed record AvailabilityRequest
{
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string[] AdditionalSchedules { get; init; } = [];
    public int IntervalMinutes { get; init; } = 30;
    public string TimeZone { get; init; } = "UTC";
}
public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool Truncated = false);
public sealed record EmailAddressDto(string? Name, string? Address);
public sealed record AccountDto(string Id, string? DisplayName, string? Mail, string? UserPrincipalName);
public sealed record MailSummaryDto(string Id, string? Subject, EmailAddressDto? Sender, DateTimeOffset? ReceivedDateTime, bool IsRead, bool HasAttachments, string? BodyPreview, bool IsDraft = false);
public sealed record MailSearchResult(IReadOnlyList<MailSummaryDto> Items, bool PossiblyTruncated);
public sealed record AttachmentMetadataDto(string Id, string? Name, string? ContentType, long Size, bool IsInline, string Kind);
public sealed record MailMessageDto(MailSummaryDto Summary, EmailAddressDto? From, IReadOnlyList<EmailAddressDto> To, IReadOnlyList<EmailAddressDto> Cc, DateTimeOffset? SentDateTime, string BodyText, bool BodyTruncated, IReadOnlyList<AttachmentMetadataDto> Attachments, bool AttachmentsTruncated, bool RecipientsTruncated, IReadOnlyList<EmailAddressDto>? Bcc = null, string? EditVersion = null);
public sealed record AttachmentContentDto(string Text, string Encoding, bool Truncated);
public sealed record AttachmentResult(AttachmentMetadataDto Metadata, AttachmentContentDto? Content, string? ContentUnavailableReason);
public sealed record CalendarDto(string Id, string? Name, bool IsDefaultCalendar);
public sealed record AttendeeDto(string? Name, string? Address, string? Type, string? Response);
public sealed record CalendarEventDto(string Id, string? Subject, DateTimeOffset Start, DateTimeOffset End, string TimeZone, bool IsAllDay, string? OriginalStartTimeZone, string? OriginalEndTimeZone, string? Location, EmailAddressDto? Organizer, IReadOnlyList<AttendeeDto> Attendees, bool AttendeesTruncated, bool IsOnlineMeeting, string? OnlineMeetingUrl, bool IsCancelled);
public sealed record ScheduleAvailabilityDto(string Address, string? Slots, string? Error);
public sealed record AvailabilityResult(DateTimeOffset Start, DateTimeOffset End, string TimeZone, int IntervalMinutes, IReadOnlyDictionary<string,string> Legend, IReadOnlyList<ScheduleAvailabilityDto> Schedules);
public sealed record ErrorDto(string Code, string Message, int? RetryAfterSeconds = null, string? RequestId = null);
