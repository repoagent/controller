# Wiki access, GraphQL limits and webhook routing

Checked 2026-10-19 for Repo Agent Connector and the controller repository (then `egil/repoagent`, now `repoagent/controller`). Implementation findings refer to [revision `6c60f8d`](https://github.com/repoagent/controller/tree/6c60f8dd670a70726c131e558dd41f21ad3e7eda). No App credentials were accessed, no private-wiki authentication test was run, and no permissions or webhook settings changed during this research.

## Findings that affect the bridge now

1. The App installation has broader permissions than the bridge's minted tokens. Token creation currently explicitly requests only `discussions: write`. Code, private wiki and other newly granted resource access need an updated token request.
2. GraphQL has a finite installation budget. The present poll reads comments, replies and reactions even for quiet or unenrolled discussions; ten-second polling is not just one request every ten seconds.
3. One GitHub App registration has one webhook URL for all installations. Independently hosted installations need a shared receiver/router or separate App registrations to receive App webhooks directly.

## Wiki access

GitHub documents installation-token authentication for HTTP Git with the repository **Contents** permission. Token creation can narrow permissions below the installation grant. An installation's `Contents: read` grant therefore does not automatically appear in every token. [Installation authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [token permissions](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app).

A wiki is a separate Git repository at `https://github.com/OWNER/REPO.wiki.git`. GitHub documents cloning after creating an initial page. Private wikis require an eligible GitHub plan. [Wiki cloning](https://docs.github.com/en/communities/documenting-your-project-with-wikis/adding-or-editing-wiki-pages#cloning-wikis-to-your-computer), [availability and visibility](https://docs.github.com/en/communities/documenting-your-project-with-wikis/about-wikis).

There is evidence against a blanket claim that Apps cannot use wikis: Microsoft's TypeScript wiki [workflow](https://github.com/microsoft/TypeScript-wiki/blob/main/.github/workflows/sync.yml) creates an App token with Contents write permission, configures HTTPS Git authentication, and runs a [script](https://github.com/microsoft/TypeScript-wiki/blob/main/.github/workflows/sync) fetching and pushing `.wiki.git`. That public-wiki integration does **not** establish private-wiki support with a read-only token. That exact combination remains unverified here.

There is no basis in the official sources reviewed to claim a classic PAT is mandatory. Its broad `repo` scope would add a separate human permission boundary. Try the existing App permission model first. [PAT limitations](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-tokens-limitations), [classic scopes](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps#available-scopes).

### Current blockers and the smallest useful test

- The target repository reported `has_wiki: false` during this check. A disabled or uninitialized wiki cannot establish authentication compatibility.
- [`GitHubAppCredentials.GetToken`](https://github.com/repoagent/controller/blob/6c60f8dd670a70726c131e558dd41f21ad3e7eda/src/DiscussionBridge/GitHubAppCredentials.cs) explicitly requests `permissions = new { discussions = "write" }`. Request `contents = "read"` alongside the existing Discussion access to test private Git reads. Other approved App permissions likewise need to be included if sessions are to use them. Alternatively, omitting the permission filter inherits the installation grants; retain the repository restriction either way.
- The [`gh` wrapper](https://github.com/repoagent/controller/blob/6c60f8dd670a70726c131e558dd41f21ad3e7eda/src/DiscussionBridge/AppGitHubCommand.cs) does not configure Git credentials. HTTPS Git needs a separate credential-helper/askpass handoff supplying the fresh App token; `GH_TOKEN` alone is insufficient.

Once an initialized private test wiki is available, mint a fresh parent-repository token with Contents read and run two read-only probes:

```bash
git ls-remote --exit-code https://github.com/repoagent/controller.git HEAD
git ls-remote --exit-code https://github.com/repoagent/controller.wiki.git HEAD
```

Use an isolated Git configuration with only the App credential, username `x-access-token`, no interactive prompts and no human credential or SSH fallback. Supply the token through a secure helper, never a URL or log. Confirm returned token permissions without printing the token. If the main repository probe fails, repair authentication first. If only the initialized wiki fails, capture the sanitized error before concluding that GitHub rejects that App/wiki combination. Public-wiki reads are not a valid private-authentication test.

If the isolated App test fails, practical alternatives are Markdown under `docs/` in the parent repository or a separately authorized human wiki-reading credential. Neither was configured here.

## GraphQL rate limits

For our non-Enterprise installation, the baseline is **5,000 points/hour per installation**, shared by the bridge and agents using its installation tokens. Query cost is at least one point; nested connections can cost more. Enterprise Cloud installations get 10,000; eligible larger non-Enterprise installations can scale to 12,500. REST has a separate primary budget. Secondary limits include 2,000 GraphQL points/minute, 100 concurrent requests shared with REST, and general content creation limits of 80/minute and 500/hour; secondary query/mutation costs differ from primary costs. [GitHub limits](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api).

Read response rate-limit headers, or include `rateLimit { limit remaining used resetAt cost }` in existing queries. Respect `retry-after`/reset and use backoff; GraphQL errors can arrive with HTTP 200. [Limit handling](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api#exceeding-the-rate-limit).

### What the present polling code costs

[`GitHub.Sources`](https://github.com/repoagent/controller/blob/6c60f8dd670a70726c131e558dd41f21ad3e7eda/src/DiscussionBridge/GitHub.cs) reads each Discussion's comments, each top-level comment's replies, and every source item's reactions in separate paginated requests. [`Bridge.Poll`](https://github.com/repoagent/controller/blob/6c60f8dd670a70726c131e558dd41f21ad3e7eda/src/DiscussionBridge/Bridge.cs) does this before its enrollment check, except for records stopped for pending/manual recovery.

For one Discussion, `C` top-level comments and `R` replies, with every connection fitting one page, a complete read poll makes:

```text
1 listing + 1 comment list + C reply lists + (1 + C + R) reaction lists
= 3 + 2C + R GraphQL requests
```

Ten comments and no replies therefore mean **23 requests per poll**, even without new input. At a hypothetical 360 polls/hour, that is at least 8,280 points before receipts or agent activity. Actual frequency is lower: [`Program.cs`](https://github.com/repoagent/controller/blob/6c60f8dd670a70726c131e558dd41f21ad3e7eda/src/DiscussionBridge/Program.cs) waits ten seconds *after* each poll completes. By comparison, a single one-point query every ten seconds would cost 360 points/hour.

The reviewed implementation throws on GraphQL errors but does not yet expose rate telemetry or implement rate-aware backoff. These are small, useful safeguards even for the PoC. Batching reads and avoiding repeated full-history scans can follow if observed usage warrants them.

## Webhooks and Cloudflare Tunnel

GitHub configures **one webhook per App registration**, not a separate callback for each installation. Changing Repo Agent Connector's callback affects that App's deliveries across installations. Repository webhooks are a separate mechanism with independently configurable URLs. [Webhook configuration](https://docs.github.com/en/webhooks/using-webhooks/creating-webhooks#creating-webhooks-for-a-github-app), [App webhook configuration API](https://docs.github.com/en/rest/apps/webhooks#update-a-webhook-configuration-for-an-app).

Discussion App-webhook payloads identify the installation and repository. A shared receiver can validate the webhook signature and route by `installation.id`, with repository routing as needed for multiple workers within one installation. Cloudflare Tunnel publishes a hostname pointing to a service; it does not add per-installation GitHub callbacks. [Payloads](https://docs.github.com/en/webhooks/webhook-events-and-payloads#discussion), [signature verification](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries), [Cloudflare routing](https://developers.cloudflare.com/tunnel/concepts/routing/).

For a single hosted service, the natural design is one receiver with installation routing. For independent self-hosted bridges, choose either a shared event relay or a separate App registration per deployment with its own tunnel URL. Independently configured repository webhooks are another option, with their own setup. These are design options; polling remains enabled and no webhook configuration was changed.
