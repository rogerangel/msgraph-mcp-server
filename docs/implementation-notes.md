# Implementation choices

## Dependency baseline

The implementation targets .NET 10. Package APIs were checked against official documentation and the restored assemblies, then exercised by the test suite. Exact direct and transitive dependencies are committed in `packages.lock.json`; restore with `--locked-mode`.

| Package | Version | Purpose |
| --- | --- | --- |
| ModelContextProtocol.AspNetCore | 2.2.0 | Official MCP Streamable HTTP server |
| Microsoft.Identity.Web | 4.15.0 | ASP.NET OIDC and supported MSAL cache-provider integration |
| Microsoft.Identity.Client | 4.90.1 | Delegated token acquisition/cache semantics |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.12 | OIDC authorization code and PKCE |
| AngleSharp | 1.8.3 | Local HTML parsing to plain text |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 | HTTP integration tests |
| xunit.v3 | 4.0.1 | Synthetic unit/integration tests |

The MCP server uses `WithHttpTransport` with `HttpServerSessionMode.Stateless` and explicit tool-class registration. No assembly-wide discovery or legacy SSE is enabled. See the [official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and [transport options](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.AspNetCore.HttpServerTransportOptions.html).

## Graph access

Direct HTTP keeps this particular surface small: domain services own fixed routes, query construction and response DTOs; one HTTP component handles origin/method checks, deadlines, byte limits, cancellation and safe-read retries. The generated Graph SDK would provide typed request builders and models, but most of that surface is unused here, and bounded raw attachment reads and DTO projection would still need explicit code. There is no Microsoft Graph SDK dependency and no request builder exposed to MCP callers.

Mail search uses Graph mail `$search`, with a bounded first page and no local mailbox scan. Calendar ranges use `calendarView` to expand occurrences. Availability uses `getSchedule`, projects only free/busy slots, and does not need a broader scope than the approved `Calendars.Read`.

Phase 1.5 replaces `Mail.Read` with delegated `Mail.ReadWrite` for four native draft-creation operations and a guarded existing-draft PATCH. It adds no `Mail.Send` or deletion capability. The persistent MSAL/Data Protection implementation is unchanged. Protected, expiring edit versions carry the literal Graph ETag without a draft database. Mutation dispatch has a separate receipt state and no automatic replay; read retries are not reused for writes. See [draft contracts](drafts.md) and the [production conditional-write prerequisite](deployment.md#enable-draft-updates-separately).

- [Mail search semantics](https://learn.microsoft.com/en-us/graph/search-query-parameter#use-search-on-message-collections)
- [Message listing and pagination](https://learn.microsoft.com/en-us/graph/api/user-list-messages?view=graph-rest-1.0)
- [Calendar view](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)
- [Free/busy permissions and behavior](https://learn.microsoft.com/en-us/graph/api/calendar-getschedule?view=graph-rest-1.0)
- [Throttling and Retry-After](https://learn.microsoft.com/en-us/graph/throttling)

## Existing implementation review

[Softeria ms-365-mcp-server](https://github.com/Softeria/ms-365-mcp-server) was reviewed for its tool filtering, permission mapping, response field projection, pagination, authentication cache and HTTP behavior. It is a broad Microsoft 365 integration, with endpoint-derived tool names, configurable scope/tool filters, device-code and HTTP OAuth modes, and credential-store/file cache options.

The useful lessons here are to make the effective permission/tool mapping inspectable, project results after `$select`, and treat pagination and public callback URLs explicitly. This service uses fourteen fixed domain-oriented names, protected bounded continuation cursors and fixed response DTOs. Calendar operations remain read-only; the narrow mail-write surface is enforced through routes, field allowlists and draft-specific guards. The default-off update gate never gates native draft creation. Authentication belongs on the private operator listener; Aperture handles MCP caller identity. Softeria is not a dependency and none of its broad tool surface or generic dispatch was copied.

## Verification boundary

The tests cover actual SDK discovery/calls over Streamable HTTP, fake Graph responses and resilience, protected state restart, account/scope pinning, operator/MCP separation, safe failure responses and draft write boundaries/receipts. They do not prove tenant consent, Conditional Access, actual Exchange sharing, live `If-Match` enforcement, or a particular installed Aperture release. Those checks are documented as explicit deployment checks in [deployment.md](deployment.md). Failed or incomplete live conditional-write validation leaves only `mail_update_draft` gated off; it does not block the four native creation tools.

## Pagination investigation

Live logs confirm that the reported `mail_list` request reaches Graph successfully and then fails while validating its continuation. The exact returned route spelling has not yet been identified. Boundary tests exercise the real service URLs, including their queries, plus calendar-list and default/owned-calendar-view pagination; synthetic alternate spellings do not prove the live response shape.

- Microsoft requires preserving the complete, opaque `@odata.nextLink` URL for the next request. It does not promise identical path spelling. [Pagination guidance](https://learn.microsoft.com/en-us/graph/best-practices-concept#pagination)
- Parenthesized well-known mail folders are documented, for example `/me/mailFolders('SentItems')/messages`. `/me` also identifies the same user as `/users/{signed-in-user-id}`. These documented alternatives do not establish which form this deployment received. [Mail overview](https://learn.microsoft.com/en-us/graph/api/resources/mail-api-overview?view=graph-rest-1.0), [user aliases](https://learn.microsoft.com/en-us/graph/api/resources/users?view=graph-rest-1.0)
- Calendar listing and both default and owned calendar views are supported operations; calendar-view responses can include continuation links. The documentation does not specify their literal canonical spelling. [List calendars](https://learn.microsoft.com/en-us/graph/api/user-list-calendars?view=graph-rest-1.0), [calendar-view paging](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0#response)
- Delegated `Calendars.Read` covers the current calendar listing, view, individual-event and `POST /me/calendar/getSchedule` operations for a work/school account; no extra permission is required. Event times default to UTC, consistent with the service's local conversion to the requested output timezone. `getSchedule` is a free/busy read despite its POST method. [Get event](https://learn.microsoft.com/en-us/graph/api/event-get?view=graph-rest-1.0), [getSchedule](https://learn.microsoft.com/en-us/graph/api/calendar-getschedule?view=graph-rest-1.0)

Event **4200** (`GraphContinuationRejected`) reports only fixed `ValidationStep`, `RouteShape`, and `Outcome` labels. It logs no URLs, IDs, query values, or paging tokens and does not authorize or rewrite routes. After deployment, reproduce the failure and inspect this event before changing continuation acceptance:

```bash
docker compose -f compose.example.yaml logs --since=10m graph-mcp 2>&1 | grep -E '"EventId":4200|"Id":4200|GraphContinuationRejected'
```
