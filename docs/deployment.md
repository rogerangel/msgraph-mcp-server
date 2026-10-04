# Deployment and operations

## Register the Entra application

1. Create a dedicated single-tenant **Web** app registration for a native member account. Do not reuse an application with broader consent.
2. Configure the Graph **delegated** permissions `User.Read`, `Mail.Read`, and `Calendars.Read`. Grant consent according to tenant policy. Add no application permissions and no `.default` scope requests.
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

Configuration uses ASP.NET environment-variable notation, for example `Graph__MaxAttachmentBytes`. All bounds have startup validation; deployment can tighten limits but cannot change allowed Graph operations or scopes. Internal `Transport__McpPort` and `Transport__OperatorPort` default to 8080/8081 and must differ. Adjust port mappings and health probes if changing them.

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
        "description": "Read-only Outlook mail and calendar for one Microsoft 365 account"
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

## Connect, reconnect, and disconnect

Visit `<OperatorBaseUrl>/operator`, submit Connect, sign in as the pinned account, and complete consent/MFA. The operator page shows connection status but no tokens or mailbox data. Login and disconnect forms use antiforgery protection.

The service uses silent acquisition after connection and refreshes through MSAL. Browser-cookie expiration does not stop background delegated access. `authentication_required` means the operator must reconnect; `access_denied` never triggers additional consent. Readiness returns 503 while disconnected or unable to acquire silently, and liveness remains independent.

Disconnect clears the protected user cache and invalidates local connection state/cookies/cursors. It is not Microsoft-wide token revocation. To revoke access at Microsoft, use the user's or tenant administrator's Entra controls and remove consent as appropriate. Do not add Graph session-revocation permissions.

Back up encrypted `/data` separately from its wrapping private key. Encryption does not protect a compromised running process with access to both mounts. Keep host permissions restrictive and logs private. Certificate expiration/rotation and Conditional Access changes require operational attention; see the authentication decision for rotation behavior.

## Graph operation matrix

The hard-coded Graph origin is `https://graph.microsoft.com/v1.0`. There is no alternate endpoint setting, redirect following, or generic request API.

| Operation | Allowed route | Scope |
| --- | --- | --- |
| Account | `GET /me` | `User.Read` |
| Mail list/search | `GET /me/messages`, `GET /me/mailFolders/{wellKnown}/messages` | `Mail.Read` |
| Message | `GET /me/messages/{messageId}` | `Mail.Read` |
| Attachment metadata | `GET /me/messages/{messageId}/attachments`, `GET /me/messages/{messageId}/attachments/{attachmentId}` | `Mail.Read` |
| Bounded text attachment | `GET /me/messages/{messageId}/attachments/{attachmentId}/$value` | `Mail.Read` |
| Calendars/ownership | `GET /me/calendars`, `GET /me/calendars/{calendarId}` | `Calendars.Read` |
| Calendar view | `GET /me/calendar/calendarView`, `GET /me/calendars/{calendarId}/calendarView` | `Calendars.Read` |
| Event | `GET /me/calendar/events/{eventId}`, `GET /me/calendars/{calendarId}/events/{eventId}` | `Calendars.Read` |
| Availability | `POST /me/calendar/getSchedule` | `Calendars.Read` |

`getSchedule` is read-only despite using POST. Availability for explicit users/rooms requires no additional Graph scope and is constrained by Exchange sharing; no event details from that response are exposed. [Microsoft getSchedule reference](https://learn.microsoft.com/en-us/graph/api/calendar-getschedule?view=graph-rest-1.0).

## Verification and optional real integration

Normal CI uses only fake Graph/token services. It never connects to Microsoft. Verify the local/container tests before deployment.

After explicitly opting into a real account deployment, manually check account identity, a small mail page, an HTML message, a small text attachment, a recurring event range and two-person availability. Confirm an unauthorized Aperture identity cannot see/call this connector; confirm direct agent-to-backend and agent-to-operator connections fail. Restart the container and repeat `account_me` without browser login. Disconnect and confirm mailbox calls fail.

Never add real-mailbox tests to automatic CI. Automated real integration, if introduced later, must require a manual workflow plus a separate explicit opt-in flag and must not print mailbox data.

The SDK uses stateless Streamable HTTP and supports protocol negotiation. Test the installed Aperture version rather than assuming compatibility from a dashboard connection status. Do not enable legacy SSE to hide a negotiation problem.
