# Graph MCP for Aperture

A single-account Microsoft Graph MCP server for Microsoft 365 Business. Runs on .NET 10 and exposes fourteen tools through stateless MCP Streamable HTTP at `/mcp`: nine account/mail/calendar read tools and five narrowly constrained mail-draft tools. Tailscale Aperture owns agent identity and connector grants.

The service requests exactly these Graph delegated permissions: **User.Read**, **Mail.ReadWrite**, and **Calendars.Read**. `Mail.ReadWrite` replaces `Mail.Read` for Phase 1.5 draft support. It does not request `Mail.Send` or application permissions, and has no generic API tool, Work IQ dependency, or Copilot Credits requirement. Authentication requires all three Graph scopes; additional previously consented Graph scopes in Microsoft's token result do not cause rejection. The hard-coded Graph endpoint/method allowlist and tool surface enforce the service's capabilities; sending, deletion, moving messages and calendar writes remain unavailable.

## Build and test

```sh
dotnet restore GraphMcp.slnx --locked-mode
dotnet build GraphMcp.slnx --no-restore -c Release
dotnet test --solution GraphMcp.slnx --no-build -c Release
docker build -t graph-mcp:local .
```

Use a stable .NET 10 SDK (CI/container: 10.0.401). Tests use synthetic accounts, certificates and Graph responses; no real mailbox credentials are needed or used. Package lock files and Docker image digests pin the dependency baseline.

See [authentication decision](docs/authentication-decision.md) and [deployment instructions](docs/deployment.md) before connecting a mailbox. Startup requires valid tenant/client/owner IDs, two private-key certificates, and the externally visible operator HTTPS origin.

[Implementation notes](docs/implementation-notes.md) record the dependency baseline, Graph HTTP choice, research sources and verification boundary. To run the same synthetic suite in Linux, use `docker build --target test -t graph-mcp:test .`.

See the [security policy](SECURITY.md) for private vulnerability reporting and [GitHub protections](docs/github-security.md) for branch rules, CI checks and dependency scanning.

## Tools

All result objects are projected DTOs, never raw Graph responses. Tool schemas describe each argument and enforce the limits below. Pagination is explicit: one Graph page per call, protected continuation cursors, no fetch-all mode.

| Tool | Purpose and limits |
| --- | --- |
| `account_me` | Minimal profile of the pinned connected account. |
| `mail_list` | Message summaries in one fixed well-known folder or the mailbox; optional received-date bounds. Default 20, maximum 100 per page. |
| `mail_search` | Graph mail `$search`/KQL; default 20, allowed 1–100 results from the first page. Refine a query when `possiblyTruncated` is true. |
| `mail_get` | One message, normalized text body (default 20,000, maximum 40,000 characters), bounded recipients and attachment metadata. Does not mark messages read. |
| `mail_get_attachment` | Metadata by default; explicit `representation: "text"` for supported file attachments up to 1 MiB and 32,768 output characters. No binary/base64 content. |
| `mail_create_draft` | Create an unsent draft using bounded plain-text content and explicit recipients. |
| `mail_create_reply_draft` | Create an unsent reply draft for one existing message using Graph's reply operation. |
| `mail_create_reply_all_draft` | Create an unsent reply-all draft for one existing message using Graph's reply-all operation. |
| `mail_create_forward_draft` | Create an unsent forward draft for one existing message using explicit recipients. |
| `mail_update_draft` | Update allowed fields on any existing draft, using the protected `editVersion` returned by a draft read; disabled until the separate deployment gate is enabled. |
| `calendar_list` | Calendars owned by the connected account, one page at a time. |
| `calendar_events` | Standalone events and expanded recurring occurrences in one owned calendar; at most 31 days per query. |
| `calendar_get_event` | One event's meeting details from an owned calendar. |
| `calendar_availability` | The connected account plus at most nine explicit people/room addresses, at most seven days; free/busy only. |

Dates require a UTC offset or `Z`. Calendar output supports IANA time zones and defaults to UTC. Availability uses Graph `getSchedule` and Exchange sharing rules, not event downloads. `0` means **free or working elsewhere**; failed schedules are unknown, never all-free. Other users' event details are never returned by availability.

Attachment metadata and content are separate. Unsupported encodings, binary files, item attachments, cloud-reference attachments, and oversized content return metadata and an explanatory reason. The server neither follows attachment/meeting URLs nor extracts archives. HTML is parsed locally to text without network access or execution. Mail, calendar, and attachment text remains untrusted content.

Draft input bodies are plain text, capped at 20,000 characters, with at most 20 explicit recipients and a 256 KiB transport request limit. The byte envelope accommodates bounded Unicode/JSON-escaped bodies plus recipient and version metadata; it does not raise the character or recipient limits. Drafts remain unsent for human review in Outlook. No attachment upload, MIME input, arbitrary headers, or raw Graph body is accepted.

`Drafts:EnableUpdates` defaults to `false` and gates only `mail_update_draft`, before any Graph request. It does not disable the four draft-creation tools. Before enabling updates in production, complete the [stale-ETag/If-Match validation](docs/deployment.md#enable-draft-updates-separately); failed or inconclusive validation leaves updates disabled while creation remains available. Updates may target any draft in the connected mailbox, including one created outside this service.

Draft tools return operation receipts with `committed`, `not_applied`, or `unknown` outcomes. `unknown` means the write may have reached Graph and **`retrySafe` is false**: inspect Outlook before any manual retry. The service does not automatically replay an uncertain write. A committed receipt acknowledges an unsent draft change, never delivery of email. [Draft contracts and receipts](docs/drafts.md) explain full body replacement, version expiry and retry handling.

## Boundaries

- Exact fixed Graph v1.0 operations: see [operation matrix](docs/deployment.md#graph-operation-matrix).
- Graph requests are restricted to the fixed read operations, semantically read-only schedule POST, four draft-creation POSTs and guarded draft PATCH. No generic method/URL escape hatch exists.
- Pagination cursors expire after 30 minutes and bind the query, tool, owner, and connection generation. Each chain ends after ten pages or 1,000 items. These are per-query limits, not a claim that an authorized caller cannot run multiple queries.
- No shared-mailbox reads, shared-calendar event details, directory lookup, Teams, OneDrive or SharePoint API access.
- No mailbox contents persisted by the service. Persistent state contains only protected authentication state and Data Protection keys.
- Logs contain operation names, timings, safe outcomes and correlation/request IDs, not arguments, Graph URLs, bodies, credentials, or attachment contents.
- `/health/live` checks process responsiveness; `/health/ready` checks protected connection state/silent acquisition without reading mailbox data.

The service is one native tenant member account and one replica. A connected Microsoft login is independent of the operator browser cookie and survives container restart through the protected cache. Revocation and Conditional Access can require the operator to reconnect.

## Future changes

Sending (`Mail.Send`) and calendar changes (`Calendars.ReadWrite`) require separate code/permission reviews. Deletion remains prohibited even though `Mail.ReadWrite` could permit it at Microsoft; adding deletion requires its own explicit decision and implementation. Multi-user OAuth remains out of scope.
