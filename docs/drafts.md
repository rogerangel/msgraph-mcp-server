# Phase 1.5: unsent mail drafts

This phase adds four native draft-creation operations and one separately gated existing-draft update. It does not send, delete or move mail. Requested delegated Graph permissions are exactly `User.Read`, `Mail.ReadWrite`, `Calendars.Read`; `Mail.ReadWrite` replaces the former `Mail.Read` grant. Authentication requires these scopes and accepts additional previously consented Graph scopes in the token result. Explicit routes, field allowlists, draft preflight and version checks enforce the narrower service behavior regardless of additional token scopes.

## Tool inputs

All input bodies are plain text. The maximum input body is 20,000 characters and the maximum number of explicit recipients is 20. A separate 256 KiB serialized HTTP request envelope accommodates JSON escapes, Unicode text and bounded recipient/version metadata; it does not increase those content limits. Deployments may tighten `Drafts` bounds or `Transport:MaxRequestBytes`, knowingly rejecting larger inputs sooner. No MIME, HTML content type, custom headers, arbitrary Graph properties or attachment uploads are accepted.

| Tool | Inputs | Graph operation |
| --- | --- | --- |
| `mail_create_draft` | `subject`, `bodyText`; optional `toRecipients`, `ccRecipients`, `bccRecipients` | `POST /me/messages` |
| `mail_create_reply_draft` | `messageId`, `bodyText` | `POST /me/messages/{id}/createReply` |
| `mail_create_reply_all_draft` | `messageId`, `bodyText` | `POST /me/messages/{id}/createReplyAll` |
| `mail_create_forward_draft` | `messageId`, `bodyText`, `toRecipients` | `POST /me/messages/{id}/createForward` |
| `mail_update_draft` | `messageId`, `editVersion`, and at least one of `subject`, `bodyText`, `toRecipients`, `ccRecipients`, `bccRecipients` | Guarded `PATCH /me/messages/{id}` |

Recipients are explicit SMTP address strings without display names. A new draft needs a nonblank subject of at most 512 characters; recipient lists may be empty while composing it. A forward requires at least one explicit To recipient. Reply/reply-all recipient selection belongs to Graph's native operation; the tools do not reconstruct a thread by copying a retrieved message. Native operations can retain existing quoted content or attachments as part of the draft; quoted-history formatting is not guaranteed and this service adds no attachment-upload capability.

Each creation uses its native single POST. It does not create a draft and then silently PATCH it, and it does not depend on `Drafts:EnableUpdates`. The separate update tool can target **any existing draft** in the connected mailbox, including drafts created manually in Outlook or another client. No service-origin marker or local draft registry is required.

Microsoft documents these delegated operations separately: [new draft](https://learn.microsoft.com/en-us/graph/api/user-post-messages?view=graph-rest-1.0), [reply draft](https://learn.microsoft.com/en-us/graph/api/message-createreply?view=graph-rest-1.0), [reply-all draft](https://learn.microsoft.com/en-us/graph/api/message-createreplyall?view=graph-rest-1.0), [forward draft](https://learn.microsoft.com/en-us/graph/api/message-createforward?view=graph-rest-1.0), and [message update](https://learn.microsoft.com/en-us/graph/api/message-update?view=graph-rest-1.0). Sending is a separate permission/operation and is not implemented.

## Existing-draft updates

`Drafts:EnableUpdates` defaults to false. When false, `mail_update_draft` returns a `not_applied` receipt with `code: "draft_updates_disabled"` before any Graph request. The other four draft tools remain available to callers with their own Aperture grants. Enable updates only after the explicit [production If-Match check](deployment.md#enable-draft-updates-separately) passes.

Read the draft with `mail_get` to obtain its protected `editVersion`. This opaque token binds the exact Graph ETag to the account, connection generation and message ID and expires after 30 minutes. Clients must return it unchanged; it is neither a raw Graph ETag nor an editable value. A missing version means the draft cannot currently be updated safely. Reconnect/logout invalidates prior versions, as it does pagination cursors. The token uses existing Data Protection and creates no additional persistent state.

The update service checks the gate and input bounds, validates `editVersion`, rereads the specific message, requires `isDraft: true`, and compares the current ETag. Only then may it issue a PATCH with the literal original `If-Match` header. A local version mismatch prevents the PATCH; an intervening edit after preflight must be rejected by Graph. It never fetches a new ETag and retries the write automatically. A message that has been sent is not eligible for an update.

Omitted fields remain unchanged; explicit null is rejected, and supplied fields replace their corresponding values. An empty subject/body string or recipient array clears that field. In particular, **`bodyText` replaces the entire body with plain text**. It does not append a comment or preserve HTML/quoted sections automatically. Read and review the existing draft before supplying a replacement. Recipient arrays similarly replace their respective lists, so use an explicit empty list only when clearing that list is intended.

## Write receipts and uncertainty

Every completed draft-tool invocation returns a shaped receipt. Graph messages, headers and raw errors are never returned wholesale.

| Field | Meaning |
| --- | --- |
| `outcome` | `committed`, `not_applied`, or `unknown` |
| `retrySafe` | Whether the server has established that no write was applied; false for committed or unknown writes |
| `reconciliationRequired` | Whether Outlook/read-only inspection is needed to establish the resulting draft state |
| `code`, `message` | Safe status/recovery guidance |
| `messageId` | The draft ID when safely known; it can be absent after an uncertain creation |
| `draft` | A bounded draft summary when available, including a protected `editVersion` where supported |
| `retryAfterSeconds` | Optional delay before another attempt after a known rejection such as throttling |

- **`committed`**: Graph acknowledged the mutation. No email was sent. `retrySafe` is false. If parsing or a later response read fails, the receipt remains committed and may omit `draft`; inspect Outlook instead of repeating the creation.
- **`not_applied`**: validation/gating/preflight rejected the attempt, or Graph definitively rejected the write. `retrySafe` is true, but it is not an instruction to blindly repeat unchanged invalid input. Follow the error, obtain a fresh edit version when needed, and respect any retry delay.
- **`unknown`**: a request may have reached Graph but no definitive outcome is available, including relevant transport failures, timeouts, cancellation or ambiguous server responses. `retrySafe` is **false** and `reconciliationRequired` is true. Inspect the known draft ID or Outlook's Drafts folder before deciding what to do. Do not repeat automatically; duplicate unsent drafts are still unwanted side effects.

No mutation is automatically replayed after dispatch, including authentication refresh, 429 handling, or transient server failures. The read preflights retain the existing safe-read retry policy. Caller cancellation after dispatch cannot establish that no write happened. If the client connection disappears entirely, a receipt may be undeliverable; treat the missing outcome as uncertain and reconcile in Outlook.

The server logs only operation category, safe outcome, timing and safe correlation/request IDs. It does not log draft subjects, recipients, bodies, access tokens, `editVersion` values or attachments. No local idempotency database, mailbox scan or automatic deletion is introduced.
