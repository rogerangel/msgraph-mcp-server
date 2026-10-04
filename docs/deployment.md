# Deployment and operations

## Register the Entra application

1. Create a dedicated single-tenant **Web** app registration for a native member account. Do not reuse an application with broader consent.
2. Configure the Graph **delegated** permissions `User.Read`, `Mail.ReadWrite`, and `Calendars.Read`. `Mail.ReadWrite` replaces `Mail.Read` for draft support; do not retain both. Grant consent according to tenant policy. Add no `Mail.Send`, application permissions, or `.default` scope requests. Existing installations must complete the [consent migration](authentication-decision.md#phase-15-consent-migration).
3. Disable implicit grants and public-client flows. Upload the public half of the client-authentication certificate. A client secret is not needed.
4. Choose `Authentication:OperatorBaseUrl`, an absolute HTTPS origin such as `https://graph-operator.example.ts.net` or `https://graph-mcp.example.ts.net:8443`. The application does not require any external port. Register the exact redirect URI **`<OperatorBaseUrl>/operator/signin-oidc`**.
5. Record tenant ID, client ID, and the expected user's object ID. The signed-in user's tenant/object/home-account IDs must match. No first-login account binding or guest-account fallback is performed.

The application requests OIDC `openid`, `profile`, and `offline_access` for this authentication flow. They do not add mailbox privileges. The browser must reach the private operator origin; Entra does not need inbound network access to the callback. Authorization code and PKCE are handled by Microsoft.Identity.Web/MSAL. See [Microsoft authorization-code guidance](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).

## Container configuration

Copy `.env.example` to `.env` and fill in identifiers and origins. Prepare `data/` writable only by container UID/GID 1654 and mount the private certificates read-only:

| Path | Contents |
| --- | --- |
| `/run/secrets/entra-client.pfx` | Entra client-authentication certificate and private key |
| `/run/secrets/cache-protection.pfx` | Separate certificate/private key protecting Data Protection keys |
| `/data/msal-cache.bin` | Protected MSAL serialization |
| `/data/owner.bin` | Protected owner connection state |
| `/data/keys/` | Certificate-encrypted Data Protection key ring |

The example uses passwordless PFX files protected by filesystem permissions. If your PFXs are password-protected, mount password files separately and set `Authentication__ClientCertificatePasswordFile` and `Authentication__DataProtectionCertificatePasswordFile`. Do not put private keys, passwords, or tokens in appsettings, environment files, Docker build arguments, or image layers. Compose bind mounts must be pre-created with ownership/permissions that allow UID 1654 access; Compose does not repair host file ownership.

```sh
docker compose -f compose.example.yaml up --build -d
```

The image runs non-root with no shell/package manager. Compose drops capabilities, makes the root filesystem read-only, and publishes both internal ports only on host loopback. Keep `/data` and the secret mounts available across restarts. Do not scale beyond one replica: the process holds an exclusive owner-state lock.

Configuration uses ASP.NET environment-variable notation, for example `Graph__MaxAttachmentBytes`. All bounds have startup validation; deployment can tighten limits but cannot change allowed Graph operations or scopes. Draft bounds default to `Drafts__MaxBodyChars=20000` and `Drafts__MaxRecipients=20`. `Drafts__EnableUpdates=false` gates only the existing-draft PATCH operation; the four creation tools remain independent. The 256 KiB HTTP request cap applies to serialized UTF-8 JSON, accommodating bounded Unicode/JSON-escaped bodies and metadata independently of the character limits. Lowering `Transport__MaxRequestBytes` can reject an otherwise valid draft input sooner. Internal `Transport__McpPort` and `Transport__OperatorPort` default to 8080/8081 and must differ. Adjust port mappings and health probes if changing them.

## Tailscale network separation

Use separate private network entry points for MCP and operator login. The default same-host example uses two externally distinct ports, but **8443 is only an example**:

```sh
tailscale serve --bg --https=443 http://127.0.0.1:8080
tailscale serve --bg --https=8443 http://127.0.0.1:8081
```

For this example, set the operator base URL to `https://graph-mcp.<tailnet>.ts.net:8443`. Alternatively, use a separate tailnet node/service identity for an operator hostname on HTTPS 443, forwarding to the operator listener. Distinct DNS names alone on one destination IP/port do not create distinct network grants. Preserve the separate destination/port grants even when using a reverse proxy.

Restrict the backend MCP destination to the Aperture host/tag. Restrict the operator destination to the operator's trusted identity/devices. Remove conflicting broad network grants; do not enable Tailscale Funnel. Agents must not reach the backend directly or reach operator endpoints. Tailscale Serve terminates HTTPS privately: [Serve documentation](https://tailscale.com/docs/reference/tailscale-cli/serve).

Set `Transport__AllowedHosts__2` to the backend MCP hostname. The configured operator origin's hostname is accepted on the operator listener. MCP origins are denied when present unless explicitly listed in `Transport__AllowedOrigins`; normal server-to-server Aperture calls do not require browser CORS.

Operator requests must be recognized as HTTPS for secure antiforgery cookies. Configure only the actual immediate proxy IP under `Transport__KnownProxies`; Tailscale Serve sends `X-Forwarded-Proto: https` for its HTTPS proxy. The Compose example uses a dedicated `172.30.99.0/24` bridge and trusts only its host gateway `172.30.99.1`. If that subnet conflicts with your deployment, change both the network configuration and trusted IP. Verify the actual proxy source address when using Docker Desktop, another runtime, or another proxy; do not solve a mismatch by trusting every network. An empty proxy list enables no forwarded-header trust, and unrecognized HTTP operator requests return 400. The OAuth redirect URI always comes from the configured external origin, never request headers.

The application gates paths by the actual internal connection port before routing/OIDC. The MCP branch is terminal and never enters Microsoft authentication middleware. Operator cookies are scoped to `/operator`, because cookie isolation does not use TCP ports.

## Aperture connector

Merge this into **Aperture configuration**, replacing the hostname and grant identity. This is not the tailnet network policy:

```json
{
  "connectors": {
    "servers": {
      "graph365": {
        "protocol": "mcp",
        "url": "https://graph-mcp.<tailnet>.ts.net/mcp",
        "description": "Outlook reading and controlled unsent drafts for one Microsoft 365 account"
      }
    }
  },
  "grants": [{
    "src": ["owner@example.com"],
    "app": {
      "tailscale.com/cap/aperture": [{
        "connectors": [
          "graph365/tools/account_me",
          "graph365/tools/mail_list",
          "graph365/tools/mail_search",
          "graph365/tools/mail_get",
          "graph365/tools/mail_get_attachment",
          "graph365/tools/calendar_list",
          "graph365/tools/calendar_events",
          "graph365/tools/calendar_get_event",
          "graph365/tools/calendar_availability"
        ]
      }]
    }
  }]
}
```

The upstream needs no bearer/OAuth connector configuration because it is reachable only across the constrained tailnet boundary. Agents connect to Aperture `/v1/mcp`, where names become `graph365_mail_list`, etc. Avoid wildcard connector grants: existing additive grants can override the intended narrow exposure. Do not grant unrelated identities access to this single owner's mailbox. [Aperture connector reference](https://tailscale.com/docs/aperture/connectors/reference).

The example above grants only the nine reading tools. Grant draft creation separately and deliberately by adding these exact connector paths to the chosen identity's grant:

```json
[
  "graph365/tools/mail_create_draft",
  "graph365/tools/mail_create_reply_draft",
  "graph365/tools/mail_create_reply_all_draft",
  "graph365/tools/mail_create_forward_draft"
]
```

The independent update grant is `graph365/tools/mail_update_draft`. Add it only for the intended editing identity after the update validation below, and set `Drafts__EnableUpdates=true` only after that validation passes. Both the grant and the application gate are required. A denied update gate returns `draft_updates_disabled` before any Graph call. Creation grants work while the update gate is false; an unsuccessful update rollout must not remove or disable the four creation capabilities. All fourteen tools remain registered so discovery and permission review are stable.

## Enable draft updates separately

Keep `DRAFT_UPDATES_ENABLED=false` in production until an explicitly authorized, manual check confirms that **the deployed Microsoft Graph mailbox endpoint enforces `If-Match`**. Synthetic tests verify request construction and conflict handling, but cannot prove Exchange's live conditional-write behavior. Microsoft's [update-message documentation](https://learn.microsoft.com/en-us/graph/api/message-update?view=graph-rest-1.0) describes PATCH and draft restrictions; treat conditional enforcement as a deployment prerequisite, not an assumption.

Perform the check with the same approved delegated app/account and Graph v1.0 endpoint, using a disposable unsent test draft and an operator-controlled HTTP test outside the MCP tool surface. Keep tokens and all mailbox content out of command history, files and logs. Do not add a generic endpoint or change scope requests to run the test.

1. Read the test draft and retain its **literal** Graph ETag A, including any weak prefix and quotes. Confirm it is a draft.
2. Change the draft in Outlook (or another explicitly authorized client), then reread it and confirm its ETag is now B, different from A. Record the current test fields for comparison.
3. Issue the allowed message PATCH using the stale **literal `If-Match: A`** and a harmless, distinguishable test field change. Confirm this PATCH actually reaches Microsoft Graph; a local `editVersion`/preflight rejection does not satisfy this check.
4. Require Graph to return **412 Precondition Failed**. Read the draft again and verify the attempted stale update was not applied. Also confirm a fresh literal ETag permits the intended controlled update.
5. Only after both checks pass, record the deployment/account/date and safe status-code/request-ID evidence, then enable `Drafts__EnableUpdates` and the separate Aperture update grant. Never record tokens, draft bodies or raw response dumps as evidence.

If the stale PATCH is accepted, the check is inconclusive, or either verification fails, leave updates disabled and investigate. Four native creation tools remain usable with their own grants. Do not bypass the requirement by refreshing the ETag and automatically retrying, stripping the weak prefix, or replacing `If-Match` with a local comparison. No live test or Microsoft login runs automatically in normal CI or application startup.

This gate is an operator attestation, not an automated Microsoft capability detector. Repeat the controlled check after material changes to conditional-write behavior or deployment assumptions. Keep deletion outside this service; dispose of test drafts manually in Outlook when finished.

## Connect, reconnect, and disconnect

Visit `<OperatorBaseUrl>/operator`, submit Connect, sign in as the pinned account, and complete consent/MFA. The operator page shows connection status but no tokens or mailbox data. Login and disconnect forms use antiforgery protection.

The service uses silent acquisition after connection and refreshes through MSAL. Browser-cookie expiration does not stop background delegated access. `authentication_required` means the operator must reconnect; `access_denied` never triggers additional consent. Readiness returns 503 while disconnected or unable to acquire silently, and liveness remains independent.

Disconnect clears the protected user cache and invalidates local connection state/cookies/cursors. It is not Microsoft-wide token revocation. To revoke access at Microsoft, use the user's or tenant administrator's Entra controls and remove consent as appropriate. Do not add Graph session-revocation permissions.

### Diagnose callback acceptance

A written MSAL cache does not establish a connected owner. Microsoft.Identity.Web can persist the token cache before the application's owner, scope, home-account and owner-state checks finish. If `/operator` remains disconnected and `owner.bin` is absent, inspect the structured events from category `GraphMcp.Auth.OidcAcceptanceDiagnostics`, event ID `4100` (`OidcAcceptanceGate`).

Each event contains only fixed `Stage`, `Gate`, `Status` and `Comparison` labels:

| Gate | Diagnostic meaning |
| --- | --- |
| `owner_identity` | `approved`, or `mismatch` with a failing comparison: `tenant`, `object`, `home-tenant`, or `home-object`. Multiple failures produce separate events. Missing claims also fail their comparison; values are never logged. This gate runs at `token_validated` and `ticket_received`. |
| `token_acquisition` | `started`, then `succeeded` or `failed` for the acceptance-time Graph token acquisition. A failure occurs before `owner.Connect`. |
| `graph_scopes` | `approved` or `mismatch` for the exact approved scope set, without listing the returned scopes. |
| `home_account` | `approved` or `mismatch` for the expected MSAL home-account ID, without logging either ID. |
| `owner_persistence` | `started`, then `succeeded` or `failed` around `owner.Connect` and protected owner-state persistence. |
| `acceptance` | `succeeded` at the end of ticket acceptance, or `failed` when the remote-failure handler runs. |
| `preceding_handler` | `failed` or `stopped` when an earlier Microsoft authentication event handler prevents application acceptance. The application does not continue after that result. |

The normal post-token stage is `ticket_received`; `Comparison` is `none` except for failed owner comparisons. These events deliberately omit exception text/objects, identity values, returned scope strings, authentication material and request data. Keep the existing production category filter instead of enabling verbose Microsoft/IdentityModel logging. Share only these safe gate events when diagnosing a failed connection.

Rejected ticket acceptance explicitly stops cookie sign-in and returns the same generic HTTP 401 login-failure response used by the remote-failure handler. The detailed reason stays in the fixed-label application events. No acceptance check is relaxed by diagnostics.

Back up encrypted `/data` separately from its wrapping private key. Encryption does not protect a compromised running process with access to both mounts. Keep host permissions restrictive and logs private. Certificate expiration/rotation and Conditional Access changes require operational attention; see the authentication decision for rotation behavior.

## Graph operation matrix

The hard-coded Graph origin is `https://graph.microsoft.com/v1.0`. There is no alternate endpoint setting, redirect following, or generic request API.

| Operation | Allowed route | Scope |
| --- | --- | --- |
| Account | `GET /me` | `User.Read` |
| Mail list/search | `GET /me/messages`, `GET /me/mailFolders/{wellKnown}/messages` | `Mail.ReadWrite` |
| Message / draft preflight | `GET /me/messages/{messageId}` | `Mail.ReadWrite` |
| Attachment metadata | `GET /me/messages/{messageId}/attachments`, `GET /me/messages/{messageId}/attachments/{attachmentId}` | `Mail.ReadWrite` |
| Bounded text attachment | `GET /me/messages/{messageId}/attachments/{attachmentId}/$value` | `Mail.ReadWrite` |
| New unsent draft | `POST /me/messages` | `Mail.ReadWrite` |
| Reply draft | `POST /me/messages/{messageId}/createReply` | `Mail.ReadWrite` |
| Reply-all draft | `POST /me/messages/{messageId}/createReplyAll` | `Mail.ReadWrite` |
| Forward draft | `POST /me/messages/{messageId}/createForward` | `Mail.ReadWrite` |
| Guarded existing-draft update | `PATCH /me/messages/{messageId}` with verified draft state and literal `If-Match` | `Mail.ReadWrite` |
| Calendars/ownership | `GET /me/calendars`, `GET /me/calendars/{calendarId}` | `Calendars.Read` |
| Calendar view | `GET /me/calendar/calendarView`, `GET /me/calendars/{calendarId}/calendarView` | `Calendars.Read` |
| Event | `GET /me/calendar/events/{eventId}`, `GET /me/calendars/{calendarId}/events/{eventId}` | `Calendars.Read` |
| Availability | `POST /me/calendar/getSchedule` | `Calendars.Read` |

`getSchedule` is read-only despite using POST. Availability for explicit users/rooms requires no additional Graph scope and is constrained by Exchange sharing; no event details from that response are exposed. [Microsoft getSchedule reference](https://learn.microsoft.com/en-us/graph/api/calendar-getschedule?view=graph-rest-1.0).

Mail read operations still need only `Mail.Read` in isolation; the deployment uses `Mail.ReadWrite` because draft creation/update require it. There is no separate `Mail.Read` grant or per-request scope escalation. None of the draft endpoints sends mail. All other message mutation routes and fields remain prohibited. See [draft contracts and operation receipts](drafts.md).

## Verification and optional real integration

Normal CI uses only fake Graph/token services. It never connects to Microsoft. Verify the local/container tests before deployment.

After explicitly opting into a real account deployment, manually check account identity, a small mail page, an HTML message, a small text attachment, a recurring event range and two-person availability. For authorized draft creation, use disposable unsent drafts and verify the four creation operations in Outlook; no delivery should occur. Validate draft updates only through the separate prerequisite above. Confirm an unauthorized Aperture identity cannot see/call this connector; confirm direct agent-to-backend and agent-to-operator connections fail. Restart the container and repeat `account_me` without browser login. Disconnect and confirm mailbox calls fail.

Never add real-mailbox tests to automatic CI. Automated real integration, if introduced later, must require a manual workflow plus a separate explicit opt-in flag and must not print mailbox data.

The SDK uses stateless Streamable HTTP and supports protocol negotiation. Test the installed Aperture version rather than assuming compatibility from a dashboard connection status. Do not enable legacy SSE to hide a negotiation problem.
