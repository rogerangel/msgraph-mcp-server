using System.Net.Mail;
using System.Text.Json;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Options;

namespace GraphMcp.Graph;

/// <summary>Five narrowly typed draft operations. Sending and deletion are deliberately absent.</summary>
public sealed class DraftService(GraphHttpClient graph, DraftEditVersionProtector versions, IOptions<DraftOptions> options) : IDraftService
{
    private readonly DraftOptions _options = options.Value;

    public async Task<DraftDto> CreateAsync(CreateDraftRequest request, DraftWriteState state, CancellationToken cancellationToken)
    {
        ValidateSubject(request.Subject, requireNonBlank: true);
        ValidateBody(request.BodyText);
        ValidateRecipients(request.ToRecipients, request.CcRecipients, request.BccRecipients);
        var generation = versions.ConnectionGeneration;
        var body = new
        {
            subject = request.Subject,
            body = TextBody(request.BodyText),
            toRecipients = Recipients(request.ToRecipients),
            ccRecipients = Recipients(request.CcRecipients),
            bccRecipients = Recipients(request.BccRecipients)
        };
        using var response = await graph.CreateDraftAsync(body, state, cancellationToken);
        return MapDraft(response.RootElement, generation);
    }

    public async Task<DraftDto> CreateReplyAsync(ReplyDraftRequest request, DraftWriteState state, CancellationToken cancellationToken)
    {
        GraphInput.Id(request.MessageId);
        ValidateBody(request.BodyText);
        var generation = versions.ConnectionGeneration;
        using var response = await graph.CreateReplyDraftAsync(request.MessageId, new { message = new { body = TextBody(request.BodyText) } }, state, cancellationToken);
        return MapDraft(response.RootElement, generation);
    }

    public async Task<DraftDto> CreateReplyAllAsync(ReplyDraftRequest request, DraftWriteState state, CancellationToken cancellationToken)
    {
        GraphInput.Id(request.MessageId);
        ValidateBody(request.BodyText);
        var generation = versions.ConnectionGeneration;
        using var response = await graph.CreateReplyAllDraftAsync(request.MessageId, new { message = new { body = TextBody(request.BodyText) } }, state, cancellationToken);
        return MapDraft(response.RootElement, generation);
    }

    public async Task<DraftDto> CreateForwardAsync(ForwardDraftRequest request, DraftWriteState state, CancellationToken cancellationToken)
    {
        GraphInput.Id(request.MessageId);
        ValidateBody(request.BodyText);
        ValidateRecipients(request.ToRecipients);
        if (request.ToRecipients.Length == 0) throw GraphOperationException.Invalid("A forward draft requires at least one recipient.");
        var generation = versions.ConnectionGeneration;
        using var response = await graph.CreateForwardDraftAsync(request.MessageId, new { message = new { body = TextBody(request.BodyText), toRecipients = Recipients(request.ToRecipients) } }, state, cancellationToken);
        return MapDraft(response.RootElement, generation);
    }

    public async Task<DraftDto> UpdateAsync(UpdateDraftRequest request, DraftWriteState state, CancellationToken cancellationToken)
    {
        if (!_options.EnableUpdates) throw new GraphOperationException("draft_updates_disabled", "Draft updates are disabled until the operator verifies conditional-update enforcement for this deployment.");
        GraphInput.Id(request.MessageId);
        if (request.Subject is null && request.BodyText is null && request.ToRecipients is null && request.CcRecipients is null && request.BccRecipients is null)
            throw GraphOperationException.Invalid("Specify at least one draft field to replace.");
        if (request.Subject is not null) ValidateSubject(request.Subject, requireNonBlank: false);
        if (request.BodyText is not null) ValidateBody(request.BodyText);
        ValidateRecipients(request.ToRecipients ?? [], request.CcRecipients ?? [], request.BccRecipients ?? []);
        var editVersion = versions.Read(request.EditVersion, request.MessageId);
        using var current = await graph.GetAsync(GraphInput.Query("me/messages/" + GraphInput.Id(request.MessageId), ("$select", "id,isDraft")), cancellationToken);
        if (!current.RootElement.Bool("isDraft")) throw new GraphOperationException("not_a_draft", "The message is no longer a draft. No update was applied.");
        if (current.RootElement.Text("id") != request.MessageId) throw new GraphOperationException("draft_conflict", "The message identity could not be verified. Read the draft again.");
        versions.EnsureCurrent(editVersion, current.RootElement.Text("@odata.etag"));
        // Omitting a property means leave it unchanged. Explicit empty values clear it.
        var patch = new Dictionary<string, object>();
        if (request.Subject is not null) patch.Add("subject", request.Subject);
        if (request.BodyText is not null) patch.Add("body", TextBody(request.BodyText));
        if (request.ToRecipients is not null) patch.Add("toRecipients", Recipients(request.ToRecipients));
        if (request.CcRecipients is not null) patch.Add("ccRecipients", Recipients(request.CcRecipients));
        if (request.BccRecipients is not null) patch.Add("bccRecipients", Recipients(request.BccRecipients));
        using var response = await graph.UpdateDraftAsync(request.MessageId, patch, editVersion.LiteralETag, state, cancellationToken, editVersion.ConnectionGeneration);
        return MapDraft(response.RootElement, editVersion.ConnectionGeneration, request.MessageId);
    }

    private DraftDto MapDraft(JsonElement value, string generation, string? expectedId = null)
    {
        var id = value.Text("id");
        if (id is null || !value.Bool("isDraft") || (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal)))
            throw new GraphOperationException("invalid_upstream_response", "Microsoft Graph did not return valid draft details. Inspect the mailbox before further action.");
        GraphInput.Id(id);
        var to = value.Array("toRecipients").ToArray();
        var cc = value.Array("ccRecipients").ToArray();
        var bcc = value.Array("bccRecipients").ToArray();
        var remaining = 100;
        IReadOnlyList<EmailAddressDto> Project(JsonElement[] recipients)
        {
            var count = Math.Min(remaining, recipients.Length);
            remaining -= count;
            return recipients.Take(count).Select(MailService.Address).Where(x => x is not null).Select(x => x!).ToArray();
        }
        var toDto = Project(to);
        var ccDto = Project(cc);
        var bccDto = Project(bcc);
        return new(id, value.BoundedText("subject", 512), true, toDto, ccDto, bccDto, value.BoundedText("bodyPreview", 512), value.Bool("hasAttachments"),
            to.Length + cc.Length + bcc.Length > 100, versions.Issue(id, value.Text("@odata.etag"), true, generation));
    }

    private static object TextBody(string content) => new { contentType = "Text", content };
    private static object[] Recipients(string[] addresses) => addresses.Select(address => (object)new { emailAddress = new { address } }).ToArray();

    private static void ValidateSubject(string subject, bool requireNonBlank)
    {
        if (subject is null || subject.Length > 512 || subject.Any(char.IsControl) || (requireNonBlank && string.IsNullOrWhiteSpace(subject)))
            throw GraphOperationException.Invalid(requireNonBlank ? "A new draft requires a nonblank subject of at most 512 characters." : "The subject must contain at most 512 characters without control characters.");
    }
    private void ValidateBody(string body)
    {
        if (body is null || body.Length > _options.MaxBodyChars || body.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw GraphOperationException.Invalid($"Draft body text must contain at most {_options.MaxBodyChars} characters; only newline and tab control characters are allowed.");
    }
    private void ValidateRecipients(params string[][] recipientLists)
    {
        if (recipientLists.Any(x => x is null) || recipientLists.Sum(x => x.Length) > _options.MaxRecipients)
            throw GraphOperationException.Invalid($"At most {_options.MaxRecipients} explicit recipients are permitted across To, Cc, and Bcc.");
        foreach (var address in recipientLists.SelectMany(x => x))
            if (string.IsNullOrWhiteSpace(address) || address.Length > 254 || address.Any(char.IsControl) || !address.Contains('@')
                || !MailAddress.TryCreate(address, out var parsed) || !string.Equals(address, parsed.Address, StringComparison.Ordinal))
                throw GraphOperationException.Invalid("Recipients must be SMTP email addresses without display names.");
    }
}
