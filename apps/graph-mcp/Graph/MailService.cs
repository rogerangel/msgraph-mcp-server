using System.Globalization;
using System.Text;
using System.Text.Json;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Options;

namespace GraphMcp.Graph;

public sealed class MailService(GraphHttpClient graph, GraphCursorProtector cursors, IOptions<GraphOptions> options, DraftEditVersionProtector editVersions) : IMailService
{
    private readonly GraphOptions _options = options.Value;
    private const string SummarySelect = "id,subject,sender,receivedDateTime,isRead,hasAttachments,bodyPreview,isDraft";
    private const string AttachmentSelect = "id,name,contentType,size,isInline,lastModifiedDateTime";
    private static readonly HashSet<string> Folders = ["inbox", "sentitems", "archive", "drafts", "deleteditems", "junkemail", "all"];

    public async Task<PageResult<MailSummaryDto>> ListAsync(MailListRequest request, CancellationToken cancellationToken)
    {
        var size = GraphInput.PageSize(request.PageSize, _options.MaxPageSize);
        if (!Folders.Contains(request.Folder)) throw GraphOperationException.Invalid("The mail folder is not supported.");
        if (request.ReceivedAfter.HasValue && request.ReceivedBefore.HasValue && request.ReceivedBefore <= request.ReceivedAfter) throw GraphOperationException.Invalid("receivedBefore must follow receivedAfter.");
        var filters = new List<string>();
        if (request.ReceivedAfter is { } after) filters.Add("receivedDateTime ge " + GraphInput.Date(after));
        if (request.ReceivedBefore is { } before) filters.Add("receivedDateTime lt " + GraphInput.Date(before));
        var url = GraphInput.Query(request.Folder == "all" ? "me/messages" : $"me/mailFolders/{request.Folder}/messages", ("$select", SummarySelect), ("$top", size.ToString(CultureInfo.InvariantCulture)), ("$orderby", "receivedDateTime desc"), ("$filter", filters.Count == 0 ? null : string.Join(" and ", filters)));
        var page = cursors.Begin(url, request with { Cursor = null }, request.Cursor);
        using var response = await graph.GetAsync(page.CurrentUrl, cancellationToken);
        var values = response.RootElement.Array("value").ToArray();
        EnsurePage(values.Length, size);
        var next = cursors.Next(page, response.RootElement.Text("@odata.nextLink"), values.Length);
        return new(values.Select(MapSummary).ToArray(), next.Cursor, next.Truncated);
    }

    public async Task<MailSearchResult> SearchAsync(MailSearchRequest request, CancellationToken cancellationToken)
    {
        var limit = GraphInput.PageSize(request.Limit, _options.MaxPageSize);
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > 512 || request.Query.Any(char.IsControl)) throw GraphOperationException.Invalid("Search query must contain 1 to 512 characters without control characters.");
        var query = request.Query.Trim();
        // This is one encoded KQL value, never an OData or URL fragment.
        var quoted = "\"" + query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var url = GraphInput.Query("me/messages", ("$select", SummarySelect), ("$search", quoted), ("$top", limit.ToString(CultureInfo.InvariantCulture)));
        using var response = await graph.GetAsync(url, cancellationToken);
        var values = response.RootElement.Array("value").ToArray();
        EnsurePage(values.Length, limit);
        return new(values.Select(MapSummary).ToArray(), values.Length == limit || response.RootElement.Text("@odata.nextLink") is not null);
    }

    public async Task<MailMessageDto> GetAsync(MailGetRequest request, CancellationToken cancellationToken)
    {
        var id = GraphInput.Id(request.MessageId);
        var generation = editVersions.ConnectionGeneration;
        if (request.MaxBodyChars is < 1 || request.MaxBodyChars > _options.MaxBodyChars) throw GraphOperationException.Invalid($"maxBodyChars must be between 1 and {_options.MaxBodyChars}.");
        using var message = await graph.GetAsync(GraphInput.Query($"me/messages/{id}", ("$select", SummarySelect + ",from,toRecipients,ccRecipients,bccRecipients,sentDateTime,body")), cancellationToken);
        using var attachments = await graph.GetAsync(GraphInput.Query($"me/messages/{id}/attachments", ("$select", AttachmentSelect), ("$top", "100")), cancellationToken);
        var value = message.RootElement;
        var body = value.Object("body");
        var normalized = await TextNormalizer.NormalizeAsync(body.Text("content"), body.Text("contentType")?.Equals("html", StringComparison.OrdinalIgnoreCase) == true, request.MaxBodyChars, cancellationToken);
        var attachmentValues = attachments.RootElement.Array("value").ToArray();
        var to = value.Array("toRecipients").ToArray();
        var cc = value.Array("ccRecipients").ToArray();
        var bcc = value.Bool("isDraft") ? value.Array("bccRecipients").ToArray() : [];
        return new(MapSummary(value), Address(value.Object("from")), to.Take(100).Select(x => Address(x)!).ToArray(), cc.Take(100).Select(x => Address(x)!).ToArray(), value.Timestamp("sentDateTime"), normalized.Text, normalized.Truncated, attachmentValues.Take(100).Select(MapAttachment).ToArray(), attachmentValues.Length > 100 || attachments.RootElement.Text("@odata.nextLink") is not null, to.Length > 100 || cc.Length > 100 || bcc.Length > 100,
            value.Bool("isDraft") ? bcc.Take(100).Select(x => Address(x)!).ToArray() : null, editVersions.Issue(value.Text("id") ?? request.MessageId, value.Text("@odata.etag"), value.Bool("isDraft"), generation));
    }

    public async Task<AttachmentResult> GetAttachmentAsync(AttachmentRequest request, CancellationToken cancellationToken)
    {
        if (request.Representation is not ("metadata" or "text")) throw GraphOperationException.Invalid("Attachment representation must be metadata or text.");
        var path = $"me/messages/{GraphInput.Id(request.MessageId)}/attachments/{GraphInput.Id(request.AttachmentId)}";
        using var response = await graph.GetAsync(GraphInput.Query(path, ("$select", AttachmentSelect)), cancellationToken);
        var metadata = MapAttachment(response.RootElement);
        if (request.Representation == "metadata") return new(metadata, null, null);
        if (metadata.Kind != "file") return new(metadata, null, "unsupported_attachment_type");
        if (metadata.Size < 0 || metadata.Size > _options.MaxAttachmentBytes) return new(metadata, null, "attachment_too_large");
        var mediaType = metadata.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (mediaType is not ("text/plain" or "text/csv" or "text/html" or "application/json")) return new(metadata, null, "binary_or_unsupported_content_type");
        byte[] bytes;
        try { bytes = await graph.GetAttachmentBytesAsync(path + "/$value", cancellationToken); }
        catch (GraphOperationException ex) when (ex.Code == "response_too_large") { return new(metadata, null, "attachment_too_large"); }
        try
        {
            if (bytes is [0xFF, 0xFE, 0, 0, ..] or [0, 0, 0xFE, 0xFF, ..]) return new(metadata, null, "unsupported_text_encoding");
            Encoding encoding = new UTF8Encoding(false, true);
            var encodingName = "utf-8";
            var offset = 0;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, true, true); encodingName = "utf-16le"; offset = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, true, true); encodingName = "utf-16be"; offset = 2; }
            var text = encoding.GetString(bytes, offset, bytes.Length - offset);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            var normalized = await TextNormalizer.NormalizeAsync(text, mediaType == "text/html", _options.MaxAttachmentTextChars, cancellationToken);
            return new(metadata, new(normalized.Text, encodingName, normalized.Truncated), null);
        }
        catch (DecoderFallbackException) { return new(metadata, null, "unsupported_text_encoding"); }
    }

    private static void EnsurePage(int count, int requested)
    {
        if (count > requested) throw new GraphOperationException("invalid_upstream_response", "Microsoft Graph exceeded the requested result limit.");
    }
    internal static EmailAddressDto? Address(JsonElement recipient)
    {
        var email = recipient.Object("emailAddress");
        return email.Text("name") is null && email.Text("address") is null ? null : new(email.BoundedText("name", 256), email.BoundedText("address", 320));
    }
    private static MailSummaryDto MapSummary(JsonElement value) => new(value.Text("id") ?? "", value.BoundedText("subject", 512), Address(value.Object("sender")), value.Timestamp("receivedDateTime"), value.Bool("isRead"), value.Bool("hasAttachments"), value.BoundedText("bodyPreview", 512), value.Bool("isDraft"));
    private static AttachmentMetadataDto MapAttachment(JsonElement value)
    {
        var kind = value.Text("@odata.type") switch { "#microsoft.graph.fileAttachment" => "file", "#microsoft.graph.itemAttachment" => "item", "#microsoft.graph.referenceAttachment" => "reference", _ => "unknown" };
        var size = value.TryGetProperty("size", out var element) && element.TryGetInt64(out var bytes) ? bytes : -1;
        return new(value.Text("id") ?? "", value.BoundedText("name", 512), value.BoundedText("contentType", 256), size, value.Bool("isInline"), kind);
    }
}
