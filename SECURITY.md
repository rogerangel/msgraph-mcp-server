# Security policy

Security fixes target the current `main` branch. Older commits and deployed images should be updated to include relevant fixes.

## Report a vulnerability

Use [GitHub's private vulnerability reporting](https://github.com/rogerangel/msgraph-mcp-server/security/advisories/new) to report a suspected vulnerability. Keep vulnerability details out of public issues and pull requests until coordinated disclosure.

Include the affected commit, a description of the impact, and reproduction steps using synthetic accounts and data where possible. Sanitized diagnostics may help identify the affected operation.

Do not include access, refresh or ID tokens; authorization codes; cookies; OAuth state or nonce values; private keys or certificate files; token-cache or Data Protection files; account identifiers; or real mailbox contents and attachments. Do not share full Microsoft authorization or pagination URLs. If credentials were exposed, revoke or rotate them through the relevant provider.

## Deployment guidance

Review the [authentication decision](docs/authentication-decision.md) and [deployment instructions](docs/deployment.md) before connecting a mailbox. Keep Microsoft credentials and persistent authentication state outside source control, image layers, CI logs and artifacts. Normal CI uses synthetic data and must not connect to a real Microsoft mailbox.
