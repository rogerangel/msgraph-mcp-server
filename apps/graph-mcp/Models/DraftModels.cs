namespace GraphMcp.Models;

public sealed record CreateDraftRequest(string Subject, string BodyText, string[] ToRecipients, string[] CcRecipients, string[] BccRecipients);
public sealed record ReplyDraftRequest(string MessageId, string BodyText);
public sealed record ForwardDraftRequest(string MessageId, string BodyText, string[] ToRecipients);
public sealed record UpdateDraftRequest(string MessageId, string EditVersion, string? Subject = null, string? BodyText = null,
    string[]? ToRecipients = null, string[]? CcRecipients = null, string[]? BccRecipients = null);

public sealed record DraftDto(string Id, string? Subject, bool IsDraft, IReadOnlyList<EmailAddressDto> To,
    IReadOnlyList<EmailAddressDto> Cc, IReadOnlyList<EmailAddressDto> Bcc, string? BodyPreview,
    bool HasAttachments, bool RecipientsTruncated, string? EditVersion);

public sealed record DraftWriteReceipt(string Outcome, bool RetrySafe, bool ReconciliationRequired,
    string Code, string Message, string? MessageId, DraftDto? Draft, int? RetryAfterSeconds = null);
