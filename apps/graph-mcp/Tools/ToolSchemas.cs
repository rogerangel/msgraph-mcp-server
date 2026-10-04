using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using ModelContextProtocol.Server;

namespace GraphMcp.Tools;

/// <summary>The finite public input contract, shared by discovery and runtime validation.</summary>
public static partial class ToolSchemas
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Schemas = BuildSchemas();

    public static void Apply(IEnumerable<McpServerTool> tools)
    {
        var registered = tools.ToArray();
        if (registered.Length != Schemas.Count || registered.Select(x => x.ProtocolTool.Name).Distinct().Count() != Schemas.Count)
            throw new InvalidOperationException("Exactly the approved tools must be registered.");
        foreach (var tool in registered)
            tool.ProtocolTool.InputSchema = Schemas.TryGetValue(tool.ProtocolTool.Name, out var schema)
                ? schema : throw new InvalidOperationException("An unapproved tool was registered.");
    }

    public static void Validate(string name, IDictionary<string, JsonElement>? arguments, GraphOptions options, DraftOptions? drafts = null)
    {
        if (!Schemas.TryGetValue(name, out var schema)) throw GraphOperationException.Invalid("Unknown tool.");
        var properties = schema.GetProperty("properties");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (arguments is null || !arguments.ContainsKey(required.GetString()!))
                throw GraphOperationException.Invalid($"Required parameter {required.GetString()} is missing.");
        if (arguments is null) return;
        foreach (var (key, value) in arguments)
        {
            // Do not echo unknown property names or input values into errors or logs.
            if (!properties.TryGetProperty(key, out var field))
                throw GraphOperationException.Invalid("Unknown parameters are not permitted.");
            ValidateValue(key, value, field);
        }
        foreach (var key in new[] { "pageSize", "limit" })
            if (arguments.TryGetValue(key, out var size) && size.GetInt32() > options.MaxPageSize)
                throw GraphOperationException.Invalid("The requested page size exceeds the configured limit.");
        if (arguments.TryGetValue("maxBodyChars", out var body) && body.GetInt32() > options.MaxBodyChars)
            throw GraphOperationException.Invalid("The requested body length exceeds the configured limit.");
        if (arguments.TryGetValue("timeZone", out var zone)) ValidateTimeZone(zone.GetString()!);
        if (name is "calendar_events" or "calendar_availability")
        {
            var start = Instant(arguments["start"].GetString()!);
            var end = Instant(arguments["end"].GetString()!);
            var maximum = name == "calendar_availability" ? 7 : Math.Min(31, options.MaxCalendarRangeDays);
            if (end <= start || end - start > TimeSpan.FromDays(maximum))
                throw GraphOperationException.Invalid($"The end must follow the start by at most {maximum} days.");
        }
        if (name == "mail_list" && arguments.TryGetValue("receivedAfter", out var after)
            && arguments.TryGetValue("receivedBefore", out var before)
            && Instant(before.GetString()!) <= Instant(after.GetString()!))
            throw GraphOperationException.Invalid("receivedBefore must follow receivedAfter.");
        if (name is "mail_create_draft" or "mail_create_reply_draft" or "mail_create_reply_all_draft" or "mail_create_forward_draft" or "mail_update_draft")
        {
            drafts ??= new DraftOptions();
            if (arguments.TryGetValue("bodyText", out var content) && content.GetString()!.Length > drafts.MaxBodyChars)
                throw GraphOperationException.Invalid("Draft bodyText exceeds the configured character limit.");
            var explicitRecipients = new[] { "toRecipients", "ccRecipients", "bccRecipients" }
                .Sum(key => arguments.TryGetValue(key, out var recipients) ? recipients.GetArrayLength() : 0);
            if (explicitRecipients > drafts.MaxRecipients)
                throw GraphOperationException.Invalid("The explicit recipient count exceeds the configured limit.");
            if (name == "mail_update_draft" && !new[] { "subject", "bodyText", "toRecipients", "ccRecipients", "bccRecipients" }.Any(arguments.ContainsKey))
                throw GraphOperationException.Invalid("Supply at least one draft field to update.");
        }
    }

    public static DateTimeOffset? OptionalInstant(string? value) => value is null ? null : Instant(value);

    public static DateTimeOffset Instant(string value)
    {
        if (value is null || !InstantPattern().IsMatch(value) || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw GraphOperationException.Invalid("Times must be RFC3339 timestamps with Z or an explicit UTC offset.");
        return parsed;
    }

    private static void ValidateValue(string key, JsonElement value, JsonElement field)
    {
        switch (field.GetProperty("type").GetString())
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String) throw Invalid(key);
                var text = value.GetString()!;
                var minimumLength = field.GetProperty("minLength").GetInt32();
                if ((minimumLength > 0 && string.IsNullOrWhiteSpace(text))
                    || text.Any(character => char.IsControl(character) && !(key == "bodyText" && character is '\r' or '\n' or '\t'))
                    || text.Length < minimumLength
                    || text.Length > field.GetProperty("maxLength").GetInt32()) throw Invalid(key);
                if (field.TryGetProperty("format", out var format))
                {
                    if (format.GetString() == "date-time") Instant(text);
                    else if (format.GetString() == "email" && (!MailAddress.TryCreate(text, out var email)
                        || !string.Equals(email.Address, text, StringComparison.Ordinal))) throw Invalid(key);
                }
                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number)
                    || number < field.GetProperty("minimum").GetInt32()
                    || number > field.GetProperty("maximum").GetInt32()) throw Invalid(key);
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > field.GetProperty("maxItems").GetInt32()
                    || (field.TryGetProperty("minItems", out var minimumItems) && value.GetArrayLength() < minimumItems.GetInt32())) throw Invalid(key);
                foreach (var item in value.EnumerateArray()) ValidateValue(key, item, field.GetProperty("items"));
                break;
            default: throw new InvalidOperationException("Unsupported input contract type.");
        }
        if (field.TryGetProperty("enum", out var allowed)
            && !allowed.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value))) throw Invalid(key);
    }

    private static void ValidateTimeZone(string name)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(name);
            if (name != "UTC" && !zone.HasIanaId) throw GraphOperationException.Invalid("Use an IANA timezone identifier or UTC.");
        }
        catch (TimeZoneNotFoundException) { throw GraphOperationException.Invalid("The timezone is not supported."); }
        catch (InvalidTimeZoneException) { throw GraphOperationException.Invalid("The timezone is not supported."); }
    }

    private static GraphOperationException Invalid(string key) => GraphOperationException.Invalid($"Parameter {key} does not match its documented constraints.");

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPattern();

    private static IReadOnlyDictionary<string, JsonElement> BuildSchemas()
    {
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["account_me"] = Schema([]),
            ["mail_list"] = Schema([], ("folder", Text(32, "Well-known folder, or all for the mailbox.", "inbox", ["inbox", "sentitems", "archive", "drafts", "deleteditems", "junkemail", "all"])),
                ("receivedAfter", Time("Earliest received timestamp with an explicit offset.")), ("receivedBefore", Time("Latest received timestamp with an explicit offset.")),
                ("pageSize", Page()), ("cursor", Cursor())),
            ["mail_search"] = Schema(["query"], ("query", Text(512, "Graph-supported mail search/KQL expression.")), ("limit", Page())),
            ["mail_get"] = Schema(["messageId"], ("messageId", Id()), ("maxBodyChars", Number(1, 40_000, 20_000, "Maximum normalized body characters."))),
            ["mail_get_attachment"] = Schema(["messageId", "attachmentId"], ("messageId", Id()), ("attachmentId", Id()),
                ("representation", Text(8, "Metadata only, or bounded textual content; binary content is unsupported.", "metadata", ["metadata", "text"]))),
            ["calendar_list"] = Schema([], ("pageSize", Page()), ("cursor", Cursor())),
            ["calendar_events"] = Schema(["start", "end"], ("start", Time("Range start, including Z or explicit offset.")),
                ("end", Time("Range end after start; maximum 31 elapsed days.")), ("calendarId", Id()), ("timeZone", Zone()), ("pageSize", Page()), ("cursor", Cursor())),
            ["calendar_get_event"] = Schema(["eventId"], ("eventId", Id()), ("calendarId", Id()), ("timeZone", Zone())),
            ["calendar_availability"] = Schema(["start", "end"], ("start", Time("Range start, including Z or explicit offset.")),
                ("end", Time("Range end after start; maximum seven elapsed days.")),
                ("additionalSchedules", new JsonObject { ["type"] = "array", ["maxItems"] = 9, ["items"] = Email(), ["default"] = new JsonArray(), ["description"] = "Up to nine explicit SMTP addresses; the connected account is always included." }),
                ("intervalMinutes", new JsonObject { ["type"] = "integer", ["minimum"] = 15, ["maximum"] = 60, ["enum"] = new JsonArray(15, 30, 60), ["default"] = 30, ["description"] = "Minutes per free/busy slot." }),
                ("timeZone", Zone())),
            ["mail_create_draft"] = Schema(["subject", "bodyText"], ("subject", Text(512, "Nonblank draft subject; no control characters.")),
                ("bodyText", DraftBody()), ("toRecipients", Recipients()), ("ccRecipients", Recipients()), ("bccRecipients", Recipients())),
            ["mail_create_reply_draft"] = Schema(["messageId", "bodyText"], ("messageId", Id()), ("bodyText", DraftBody())),
            ["mail_create_reply_all_draft"] = Schema(["messageId", "bodyText"], ("messageId", Id()), ("bodyText", DraftBody())),
            ["mail_create_forward_draft"] = Schema(["messageId", "bodyText", "toRecipients"], ("messageId", Id()),
                ("bodyText", DraftBody()), ("toRecipients", Recipients(minimum: 1))),
            ["mail_update_draft"] = Schema(["messageId", "editVersion"], ("messageId", Id()),
                ("editVersion", Text(16_384, "Opaque current version returned by mail_get or a draft result for this same draft; expires after 30 minutes.")),
                ("subject", ClearableText(512, "Replacement subject. Empty clears it; omit to preserve. Null is rejected.")),
                ("bodyText", DraftBody()), ("toRecipients", Recipients()), ("ccRecipients", Recipients()), ("bccRecipients", Recipients()))
        };
    }

    private static JsonElement Schema(string[] required, params (string Name, JsonObject Schema)[] fields)
    {
        var properties = new JsonObject();
        foreach (var field in fields) properties.Add(field.Name, field.Schema);
        var node = new JsonObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = JsonSerializer.SerializeToNode(required), ["additionalProperties"] = false
        };
        return JsonSerializer.SerializeToElement(node);
    }
    private static JsonObject Text(int maximum, string description, string? defaultValue = null, string[]? allowed = null)
    {
        var schema = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximum, ["description"] = description };
        if (defaultValue is not null) schema["default"] = defaultValue;
        if (allowed is not null) schema["enum"] = JsonSerializer.SerializeToNode(allowed);
        return schema;
    }
    private static JsonObject Number(int minimum, int maximum, int defaultValue, string description) =>
        new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum, ["default"] = defaultValue, ["description"] = description };
    private static JsonObject Id() => Text(2_048, "Opaque ID returned by a previous tool; not a URL.");
    private static JsonObject Page() => Number(1, 100, 20, "Maximum items; the configured deployment limit may be lower.");
    private static JsonObject Cursor() => Text(16_384, "Opaque continuation cursor from the same tool; retain all other parameters.");
    private static JsonObject Zone() => Text(128, "IANA timezone identifier or UTC.", "UTC");
    private static JsonObject Time(string description)
    {
        var schema = Text(40, description);
        schema["format"] = "date-time";
        schema["pattern"] = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$";
        return schema;
    }
    private static JsonObject Email()
    {
        var schema = Text(254, "Explicit SMTP address, without a display name.");
        schema["format"] = "email";
        return schema;
    }
    private static JsonObject ClearableText(int maximum, string description)
    {
        var schema = Text(maximum, description);
        schema["minLength"] = 0;
        return schema;
    }
    private static JsonObject DraftBody() => ClearableText(20_000,
        "Literal plain-text content, at most 20000 characters or the configured lower limit. Newlines/tabs are allowed. Empty clears/creates an empty body. For updates this replaces the entire body; omit to preserve.");
    private static JsonObject Recipients(int minimum = 0) => new()
    {
        ["type"] = "array", ["minItems"] = minimum, ["maxItems"] = 20, ["items"] = Email(),
        ["description"] = "SMTP addresses only, no display names. At most 20 explicitly supplied addresses across To/Cc/Bcc, or a configured lower limit. For updates, this replaces the list; empty clears it and omission preserves it."
    };
}
