# Graph MCP for Aperture

A single-account, read-only Microsoft Graph MCP server for Microsoft 365 Business. Runs on .NET 10 and exposes nine tools through stateless MCP Streamable HTTP at `/mcp`. Tailscale Aperture owns agent identity and connector grants.

Graph delegated permissions are exactly **User.Read**, **Mail.Read**, and **Calendars.Read**. There are no Graph application permissions, write tools, generic API tools, Work IQ dependencies, or Copilot Credits requirements.

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

## Tools

All result objects are projected DTOs, never raw Graph responses. Tool schemas describe each argument and enforce the limits below. Pagination is explicit: one Graph page per call, protected continuation cursors, no fetch-all mode.

| Tool | Purpose and limits |
| --- | --- |
| `account_me` | Minimal profile of the pinned connected account. |
| `mail_list` | Message summaries in one fixed well-known folder or the mailbox; optional received-date bounds. Default 20, maximum 100 per page. |
| `mail_search` | Graph mail `$search`/KQL; default 20, allowed 1–100 results from the first page. Refine a query when `possiblyTruncated` is true. |
| `mail_get` | One message, normalized text body (default 20,000, maximum 40,000 characters), bounded recipients and attachment metadata. Does not mark messages read. |
| `mail_get_attachment` | Metadata by default; explicit `representation: "text"` for supported file attachments up to 1 MiB and 32,768 output characters. No binary/base64 content. |
| `calendar_list` | Calendars owned by the connected account, one page at a time. |
| `calendar_events` | Standalone events and expanded recurring occurrences in one owned calendar; at most 31 days per query. |
| `calendar_get_event` | One event's meeting details from an owned calendar. |
| `calendar_availability` | The connected account plus at most nine explicit people/room addresses, at most seven days; free/busy only. |

Dates require a UTC offset or `Z`. Calendar output supports IANA time zones and defaults to UTC. Availability uses Graph `getSchedule` and Exchange sharing rules, not event downloads. `0` means **free or working elsewhere**; failed schedules are unknown, never all-free. Other users' event details are never returned by availability.

Attachment metadata and content are separate. Unsupported encodings, binary files, item attachments, cloud-reference attachments, and oversized content return metadata and an explanatory reason. The server neither follows attachment/meeting URLs nor extracts archives. HTML is parsed locally to text without network access or execution. Mail, calendar, and attachment text remains untrusted content.

## Boundaries

- Exact fixed Graph v1.0 operations: see [operation matrix](docs/deployment.md#graph-operation-matrix).
- Only Graph GETs and the semantically read-only `POST /me/calendar/getSchedule` are implemented.
- Pagination cursors expire after 30 minutes and bind the query, tool, owner, and connection generation. Each chain ends after ten pages or 1,000 items. These are per-query limits, not a claim that an authorized caller cannot run multiple queries.
- No shared-mailbox reads, shared-calendar event details, directory lookup, Teams, OneDrive or SharePoint API access.
- No mailbox contents persisted by the service. Persistent state contains only protected authentication state and Data Protection keys.
- Logs contain operation names, timings, safe outcomes and correlation/request IDs, not arguments, Graph URLs, bodies, credentials, or attachment contents.
- `/health/live` checks process responsiveness; `/health/ready` checks protected connection state/silent acquisition without reading mailbox data.

The service is one native tenant member account and one replica. A connected Microsoft login is independent of the operator browser cookie and survives container restart through the protected cache. Revocation and Conditional Access can require the operator to reconnect.

## Future changes

No configuration flag enables writes or broader scopes. Draft creation (`Mail.ReadWrite`), sending (`Mail.Send`), and calendar changes (`Calendars.ReadWrite`) each require a separate code/permission review. Deletion and multi-user OAuth remain separate future decisions.
