# Single-account delegated Microsoft authentication

Decision: use Microsoft's ASP.NET Core OIDC handler, Microsoft.Identity.Web and MSAL with authorization code + PKCE. This is a confidential server client authenticated with a mounted certificate, not an application-permission Graph client. The delegated Graph scope set is fixed in code: `User.Read`, `Mail.Read`, `Calendars.Read`. OIDC uses `openid`, `profile`, `offline_access`; no `.default`, device-code fallback, client-credentials acquisition, Work IQ or Copilot Credits.

Device code is supported for public clients and avoids a callback and client credential, but organizations may block it and Microsoft recommends restricting it. A desktop public-client PKCE helper would require a separate provisioning/cache-transfer workflow. OBO requires a delegated Entra assertion from an upstream API client, which Aperture does not supply here. Managed identity and client credentials do not provide the delegated mailbox access required. A protected server credential and private browser callback are the smallest supported fit for this deployment.

## Registration and external URL

Create a dedicated, single-tenant Entra registration; disable implicit grants and public-client flows. Add only the three delegated Graph scopes above, obtain whatever consent the tenant requires, upload the client certificate's public half, and restrict assignment to the owner where supported. Configure the owner's **native member** tenant ID and object ID (guest accounts are excluded). Do not add an app permission or a secret. Audit prior grants on a reused registration; use a new registration when unsure.

`Authentication:OperatorBaseUrl` is an external HTTPS **origin**, for example `https://operator.example.ts.net` or `https://operator.example.ts.net:9443`. Register that exact origin plus `/operator/signin-oidc` as a **Web** redirect. There is no required external port. Both the OIDC challenge and MSAL code redemption use this configured URL. Keep the internal MCP/operator listeners separate, and use Tailscale grants to keep agents/Aperture off the operator listener. Reverse proxies must preserve the `/operator` path and trust only the configured forwarding proxy.

The operator page has antiforgery-protected login and disconnect forms. Microsoft middleware validates state, nonce, code and tokens; the application pins both tenant/local user IDs and MSAL home IDs. The service releases a Graph access token only when MSAL reports exactly the approved resource scopes, including cached results. Tokens remain opaque. No account, token or caller identity supplied by an MCP client selects the Microsoft account.

## Persistent cache and lifecycle

The custom `ProtectedFileTokenCache` is a small adapter over Microsoft's supported `MsalAbstractTokenCacheProvider` hooks. MSAL owns token serialization, refresh and cache semantics. ASP.NET Core Data Protection encrypts/authenticates the serialized bytes; its persisted key ring is itself wrapped using a separate mounted private certificate. The base provider is deliberately not given a protector because its backward-compatible unprotect fallback permits plaintext. Our read hook unprotects first and fails closed.

The cache is bound to the configured client and native MSAL account ID. Owner state stores only a connected bit and random connection generation under a distinct Data Protection purpose; identity claims `oid`, `tid`, `uid`, `utid` are reconstructed from the pinned configuration after restart. Browser cookies are not needed for background acquisition. Cache and owner files use atomic replacement, owner-only permissions and a bounded size; the parent directory is owner-only. The key ring, ciphertext and the private wrapping certificate must remain available across restarts. Losing any required key requires reauthentication; no plaintext recovery path exists.

A process-wide lifecycle gate covers complete MSAL acquisitions, callback handling and logout; a file lock rejects a second process using the owner state. Only one replica is supported. Logout first invalidates the in-memory generation and removes the old connected owner record, then persists the disconnected state and clears cache bytes. It attempts cache removal even if the state write fails. Missing owner state is disconnected on restart. Concurrent acquisitions/callbacks cannot restore old tokens. Old operator cookies, pagination cursors and pre-logout login attempts become invalid. MSAL clears in-memory state from the empty disk cache on its next access. The operator can reconnect at any time; successful reconnect also rotates the generation.

Silent token acquisition propagates tool cancellation to Microsoft.Identity.Web/MSAL. Expired or revoked refresh tokens and Conditional Access challenges become a safe `authentication_required` error. A tool never starts interactive authentication or broadens consent. The operator must complete browser login again. Local disconnect does **not** revoke already issued Microsoft access tokens or all account sessions: use Microsoft user/admin revocation outside this application when required, without adding revocation scopes here.

The operator cookie expires after one hour. If its expiry or tenant policy prevents using the disconnect form, stop the service and remove only the encrypted token-cache and owner-state files, then restart; retain the key ring and wrapping certificate. Never edit token files or dump them into diagnostics.

If disconnect returns an error, treat it as incomplete: stop the service, repair volume permissions/storage, and remove both protected state files before restarting. The running process invalidates its in-memory connection before disk operations, but a filesystem that refuses both state and cache deletion can retain old connected state on disk. Do not restart that state assuming the failed disconnect succeeded.

## Operations and limits

Mount passwordless PFX files as restrictive read-only Docker secrets, or supply each PFX password via its separate `*PasswordFile` setting. Neither passwords nor PFX contents belong in environment variables, source control, image layers or application settings. Keep both private certificates outside the writable `/data` mount. Run non-root with a read-only root filesystem. Rotate the Entra client certificate by uploading the new public certificate, updating its secret and restarting before removing the previous public certificate.

For a wrapping-certificate change, the small Phase 1 implementation deliberately requires an operator maintenance window and reauthentication: stop, securely remove cache/owner state and the old key ring, replace the certificate, restart, reconnect. Do not overwrite the wrapping certificate while retaining a key ring that only the old key decrypts. Back up the key ring/ciphertext and private certificate through separate protected backup channels.

CI uses synthetic identities, keys and token results only. Live tenant login, silent-refresh/restart and revocation checks are explicit deployment smoke tests; none run in ordinary CI. Tenant Conditional Access and existing consent can change at any time, so indefinite unattended access cannot be promised.

## Official sources

- [Microsoft public vs confidential clients](https://learn.microsoft.com/en-us/entra/msal/msal-client-applications)
- [Authorization code flow and PKCE](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow)
- [Device-code controls](https://learn.microsoft.com/en-us/entra/identity/conditional-access/concept-authentication-flows)
- [Token acquisition and cache behavior](https://learn.microsoft.com/en-us/entra/identity-platform/msal-acquire-cache-tokens)
- [MSAL cache-provider extension hooks](https://github.com/AzureAD/microsoft-identity-web/blob/master/src/Microsoft.Identity.Web.TokenCache/MsalAbstractTokenCacheProvider.cs)
- [Data Protection configuration and certificate key encryption](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0)
- [Refresh-token expiration and revocation](https://learn.microsoft.com/en-us/entra/identity-platform/refresh-tokens)
