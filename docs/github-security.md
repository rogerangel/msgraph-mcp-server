# GitHub repository protections

The public repository uses a [default-branch ruleset](https://github.com/rogerangel/msgraph-mcp-server/rules/24460791). Its reproducible configuration is [main.ruleset.json](../.github/main.ruleset.json); editing that file alone does not update GitHub settings.

## Pull requests and CI

Changes to `main` require a pull request, passing required GitHub Actions checks against the current base branch, and resolution of review conversations. Force pushes and branch deletion are blocked. The ruleset has no bypass actors. Repository administrators can still edit the rules themselves.

The required `test` check runs locked NuGet restore, Release build, the synthetic test suite and a Docker build. Keep this job name stable, or update the ruleset together with a job rename. Actions are pinned to verified commit SHAs; Dependabot proposes updates. Checkout does not persist credentials, and workflow tokens default to read-only without permission to approve pull requests.

The required `dependency-review` check rejects newly introduced high or critical dependency vulnerabilities, including development dependencies. It has no write token or pull-request comment permission. It does not impose a license policy. Both named checks are bound to the GitHub Actions app as their expected source.

The repository currently has one writer, so pull requests require zero additional approving reviews. This allows the maintainer to merge their own work after checks pass. When a second regular reviewer is available, consider requiring one approval and approval of the latest push. Changes to GitHub settings require administrative access; these files do not grant it.

## Security settings

- Secret scanning and push protection are enabled.
- Dependabot alerts and security update pull requests are enabled. Weekly version updates cover NuGet, GitHub Actions and Docker; updates are not automatically merged.
- CodeQL default setup analyzes C# and GitHub Actions on supported push/pull-request events and a weekly schedule. The ruleset requires CodeQL results and blocks qualifying code-scanning errors and high/critical security findings. It is managed in GitHub's code-scanning settings rather than a second checked-in CodeQL workflow.
- Private vulnerability reporting is enabled. Follow [SECURITY.md](../SECURITY.md) for safe reports.
- Only GitHub-owned actions are allowed. All external contributors require maintainer approval before fork pull-request workflows run. Review workflow and build-script changes before approving execution.

Normal CI has no real mailbox credentials and uses synthetic data. Authentication caches, environment files and private certificate material are excluded from both Git and Docker context. These exclusions cover future accidental additions; they do not remove files already tracked or erase Git history.

## Verify the live settings

Use the repository's [rules page](https://github.com/rogerangel/msgraph-mcp-server/settings/rules), [security settings](https://github.com/rogerangel/msgraph-mcp-server/settings/security_analysis) and [Actions settings](https://github.com/rogerangel/msgraph-mcp-server/settings/actions). The ruleset file records merge gates; other repository settings are managed separately and should be checked after changing ownership, visibility or plans.

GitHub documents [repository rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets), [CodeQL default setup](https://docs.github.com/en/code-security/code-scanning/enabling-code-scanning/configuring-default-setup-for-code-scanning), and [dependency review](https://github.com/actions/dependency-review-action).
