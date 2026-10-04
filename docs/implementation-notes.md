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

The MCP server uses `WithHttpTransport` with `HttpServerSessionMode.Stateless` and explicit registration of three tool classes. No assembly-wide discovery or legacy SSE is enabled. See the [official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and [transport options](https://csharp.sdk.modelcontextprotocol.io/api/ModelContextProtocol.AspNetCore.HttpServerTransportOptions.html).

## Graph access

Direct HTTP keeps this particular surface small: three services own fixed routes, query construction and response DTOs; one HTTP component handles origin/method checks, deadlines, byte limits, cancellation and retries. The generated Graph SDK would provide typed request builders and models, but most of that surface is unused here, and bounded raw attachment reads and DTO projection would still need explicit code. There is no Microsoft Graph SDK dependency and no request builder exposed to MCP callers.

Mail search uses Graph mail `$search`, with a bounded first page and no local mailbox scan. Calendar ranges use `calendarView` to expand occurrences. Availability uses `getSchedule`, projects only free/busy slots, and does not need a broader scope than the approved `Calendars.Read`.

- [Mail search semantics](https://learn.microsoft.com/en-us/graph/search-query-parameter#use-search-on-message-collections)
- [Message listing and pagination](https://learn.microsoft.com/en-us/graph/api/user-list-messages?view=graph-rest-1.0)
- [Calendar view](https://learn.microsoft.com/en-us/graph/api/calendar-list-calendarview?view=graph-rest-1.0)
- [Free/busy permissions and behavior](https://learn.microsoft.com/en-us/graph/api/calendar-getschedule?view=graph-rest-1.0)
- [Throttling and Retry-After](https://learn.microsoft.com/en-us/graph/throttling)

## Existing implementation review

[Softeria ms-365-mcp-server](https://github.com/Softeria/ms-365-mcp-server) was reviewed for its tool filtering, permission mapping, response field projection, pagination, authentication cache and HTTP behavior. It is a broad Microsoft 365 integration, with endpoint-derived tool names, configurable scope/tool filters, device-code and HTTP OAuth modes, and credential-store/file cache options.

The useful lessons here are to make the effective permission/tool mapping inspectable, project results after `$select`, and treat pagination and public callback URLs explicitly. This service uses nine fixed domain-oriented names, protected bounded continuation cursors and fixed response DTOs. Read-only enforcement is structural in the service routes and scopes, with no runtime switch enabling writes. Authentication belongs on the private operator listener; Aperture handles MCP caller identity. Softeria is not a dependency and none of its broad tool surface or generic dispatch was copied.

## Verification boundary

The tests cover actual SDK discovery/calls over Streamable HTTP, fake Graph responses and resilience, protected state restart, account/scope pinning, operator/MCP separation, and safe failure responses. They do not prove tenant consent, Conditional Access, actual Exchange sharing, or a particular installed Aperture release. Those checks are documented as explicit deployment checks in [deployment.md](deployment.md).
