using System.ComponentModel;
using System.Text.Json;
using GraphMcp.Graph;
using GraphMcp.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GraphMcp.Tools;

public sealed class DraftTools(IDraftService drafts, ToolExecutor executor)
{
    [McpServerTool(Name = "mail_create_draft", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(DraftWriteReceipt))]
    [Description("Create one unsent draft in the connected account's Drafts folder. Requires a nonblank subject and plain-text body of at most 20000 characters; body may be empty. At most 20 explicitly supplied recipients across To/Cc/Bcc. Does not send, attach files, or set sender/headers. Inspect the outcome receipt: committed or unknown writes must never be repeated automatically.")]
    public Task<CallToolResult> Create(RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteWriteAsync("mail_create_draft", context, (state, ct) =>
        {
            var args = context.Params.Arguments!;
            return drafts.CreateAsync(new CreateDraftRequest(args["subject"].GetString()!, args["bodyText"].GetString()!,
                Recipients(args, "toRecipients") ?? [], Recipients(args, "ccRecipients") ?? [], Recipients(args, "bccRecipients") ?? []), state, ct);
        }, cancellationToken);

    [McpServerTool(Name = "mail_create_reply_draft", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(DraftWriteReceipt))]
    [Description("Create one unsent native reply draft to a specific message, with up to 20000 characters of literal plain-text content. Microsoft Graph chooses reply recipients and threading; quoted-history formatting is not guaranteed. Does not send. Do not repeat automatically after a committed or unknown outcome.")]
    public Task<CallToolResult> Reply(RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteWriteAsync("mail_create_reply_draft", context, (state, ct) => drafts.CreateReplyAsync(
            new ReplyDraftRequest(context.Params.Arguments!["messageId"].GetString()!, context.Params.Arguments["bodyText"].GetString()!), state, ct), cancellationToken);

    [McpServerTool(Name = "mail_create_reply_all_draft", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(DraftWriteReceipt))]
    [Description("Create one unsent native reply-all draft to a specific message. Microsoft Graph chooses recipients and threading; review recipients before any later manual sending. Accepts up to 20000 characters of plain text; quoted-history formatting is not guaranteed. Does not send. Do not repeat automatically after a committed or unknown outcome.")]
    public Task<CallToolResult> ReplyAll(RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteWriteAsync("mail_create_reply_all_draft", context, (state, ct) => drafts.CreateReplyAllAsync(
            new ReplyDraftRequest(context.Params.Arguments!["messageId"].GetString()!, context.Params.Arguments["bodyText"].GetString()!), state, ct), cancellationToken);

    [McpServerTool(Name = "mail_create_forward_draft", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(DraftWriteReceipt))]
    [Description("Create one unsent native forwarding draft from a specific message with 1 to 20 explicit To recipients and up to 20000 characters of plain-text content. Microsoft Graph creates the forwarded content; quoted-history formatting is not guaranteed. Does not send or upload attachments. Do not repeat automatically after a committed or unknown outcome.")]
    public Task<CallToolResult> Forward(RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteWriteAsync("mail_create_forward_draft", context, (state, ct) =>
        {
            var args = context.Params.Arguments!;
            return drafts.CreateForwardAsync(new ForwardDraftRequest(args["messageId"].GetString()!, args["bodyText"].GetString()!,
                Recipients(args, "toRecipients")!), state, ct);
        }, cancellationToken);

    [McpServerTool(Name = "mail_update_draft", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(DraftWriteReceipt))]
    [Description("Update allowed fields of any existing unsent draft in the connected mailbox, only when the deployment's update gate is enabled. Requires a current editVersion from mail_get or a draft result. Supply at least one field; omitted fields stay unchanged, empty strings/arrays clear them, and null is rejected. bodyText replaces the ENTIRE body with plain text, including existing quotes/signatures/formatting. Maximum 20000 body characters and 20 explicit recipients. Never sends, modifies sent/received messages, or changes attachments. On version conflict, reread and review; committed or unknown writes must not be repeated automatically.")]
    public Task<CallToolResult> Update(RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken = default) =>
        executor.ExecuteWriteAsync("mail_update_draft", context, (state, ct) =>
        {
            var args = context.Params.Arguments!;
            return drafts.UpdateAsync(new UpdateDraftRequest(args["messageId"].GetString()!, args["editVersion"].GetString()!,
                OptionalText(args, "subject"), OptionalText(args, "bodyText"), Recipients(args, "toRecipients"),
                Recipients(args, "ccRecipients"), Recipients(args, "bccRecipients")), state, ct);
        }, cancellationToken);

    // ToolSchemas validates before these callbacks run. Explicit extraction preserves omission versus
    // clearing while ensuring SDK argument binding cannot bypass the structured not_applied receipt.
    private static string? OptionalText(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) ? value.GetString()! : null;
    private static string[]? Recipients(IDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : null;
}
