using GraphMcp.Models;

namespace GraphMcp.Graph;

public interface IAccountService { Task<AccountDto> GetMeAsync(CancellationToken cancellationToken); }
public interface IMailService
{
    Task<PageResult<MailSummaryDto>> ListAsync(MailListRequest request, CancellationToken cancellationToken);
    Task<MailSearchResult> SearchAsync(MailSearchRequest request, CancellationToken cancellationToken);
    Task<MailMessageDto> GetAsync(MailGetRequest request, CancellationToken cancellationToken);
    Task<AttachmentResult> GetAttachmentAsync(AttachmentRequest request, CancellationToken cancellationToken);
}
public interface ICalendarService
{
    Task<PageResult<CalendarDto>> ListAsync(PageRequest request, CancellationToken cancellationToken);
    Task<PageResult<CalendarEventDto>> GetEventsAsync(CalendarEventsRequest request, CancellationToken cancellationToken);
    Task<CalendarEventDto> GetEventAsync(CalendarEventRequest request, CancellationToken cancellationToken);
    Task<AvailabilityResult> GetAvailabilityAsync(AvailabilityRequest request, CancellationToken cancellationToken);
}
