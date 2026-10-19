# GitHub App authentication

The bridge and its native Codex sessions publish as a GitHub App installation. GitHub user sign-in is needed only for a human/test participant, not for the agent. Codex authentication is separate and still uses its normal sign-in.

## Configure the App

1. Open the [App settings](https://github.com/settings/apps/discussion-agent-bridge). The configured App has ID `5387625` and client ID `Iv23liKvyvzZ60qGguDm`; either identifier can be supplied to the bridge. Other Apps are supported through configuration.
2. Grant repository **Discussions: read/write** permission. Install the App on the target repository and accept updated permissions if the installation already exists. Polling does not require active webhooks or an inbound endpoint.
3. Generate/download a PEM private key from the App settings. Keep its contents private and mount that one file read-only into the bridge container. Do not put it in an image, source checkout, log, issue or conversation. Mount the state directory as a separate persistent volume; keep the key outside the agent workspace.
4. In a Linux container with the .NET 11 RC2 runtime and `gh`, invoke the bridge with `--repo OWNER/REPO --app-id APP_ID_OR_CLIENT_ID --private-key /run/secrets/github-app.pem --state /data/bridge-state.json`. The normal conversation runtime also needs `codex`, its sign-in, a workspace and the native response skill.

The bridge authenticates the App, discovers the installation for the selected repository, checks Discussions permission and mints a token narrowed to that repository. `--installation-id ID` optionally verifies the expected installation. A GraphQL viewer check confirms the actual bot login matches the authenticated App before any native session is started.

`--check-auth` performs this validation and creates the authenticated `gh` wrapper, then exits without launching Codex or enrolling/forwarding Discussions. It is useful before the first conversation run. Runtime sign-in, permission and networking failures must be fixed in the configured container; the bridge does not fall back to a personal GitHub login.

## Renewal and native commands

GitHub [installation tokens expire after one hour](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation). The bridge checks the credential every minute independently of polling, as well as before every GitHub operation, and renews it when five minutes or less remain. This continues during long polls and while native work is active. The returned expiry is authoritative. A signed short-lived JWT authenticates App and token-issuance requests; the signing key is not passed as a native process environment variable.

The current token and expiry are written atomically to an owner-only file in `<state-file>.github-app`. Its directories are owner-only too. This directory also contains isolated `gh` configuration and a small native command wrapper. It must remain readable at the same paths while native sessions run. It is a credential directory, separate from the JSON conversation checkpoint; persist/mount its containing directory rather than binding an individual token file that gets replaced.

Native Codex receives the wrapper's absolute path in its standing instructions and the wrapper directory at the front of `PATH`. The response skill uses the absolute path so a login shell cannot reset command lookup. The wrapper starts the bridge's `github-command` entry point, reads the current cache on every invocation, replaces inherited GitHub tokens, selects `github.com` and uses isolated `gh` configuration before executing the real CLI. The native app-server itself does not inherit a static token that will later expire. Other normal `gh` arguments, stdin, stdout and exit status pass through.

If token renewal fails, the bridge removes the cached credential and logs a redacted authentication error. Native commands reject a missing token or a token with less than 30 seconds left before starting `gh`. There is no fallback to ambient `GH_TOKEN`, `GITHUB_TOKEN` or host `gh` sign-ins. The bridge must keep running for automatic renewal; `--check-auth` does not leave a credential-refresh daemon behind. Stop/start the bridge to reconfigure an App or key.

The wrapper is the supported GitHub command path. Directly invoking another `gh` binary bypasses it and is not supported for agent publication. This is credential plumbing within the configured runtime, not a security boundary against arbitrary code running with the same operating-system identity.

## Bot identity and existing state

The bot name is derived from the registered App. Choosing an App with a shorter name shortens its actual bot handle; a local text alias would not register that identity with GitHub. For example, `@bridge` belongs to an unrelated real GitHub user and must not be used as this App's identity. No arbitrary mention alias is configured by this bridge.

Mention the actual bot login in polled text, for example `@discussion-agent-bridge[bot]`, or enroll the Discussion URL explicitly. Literal mention matching includes the suffix; autocomplete/notification behavior is not needed for enrollment. GitHub's GraphQL viewer exposes the suffixed login, while Discussion authors can expose a `Bot` with the bare App slug. Loop suppression recognizes both forms; the bare slug is accepted only for a `Bot`, so a human with that name remains participant activity. A human reaction/reply to a bot-authored post is still user activity.

A checkpoint is bound to its repository and authenticated actor. Start with a fresh state file when converting an existing user-backed run to an App, or review/migrate it deliberately with the process stopped. Automatic migration could replay historical instructions or misclassify previous user output, so the bridge rejects mismatched identity. Preserve the old checkpoint and native history for manual inspection. Native session history and compaction remain owned by Codex.

## Native GitHub agent UI

GitHub's [agent apps](https://docs.github.com/en/copilot/concepts/agent-capabilities/about-agents/agent-apps) can appear in issue Assignees, pull request mention autocomplete and the Agents UI. This requires an App configured with agent features, not just a normal App installation or a local text alias. GitHub also advertises [early access for developers creating agent apps](https://github.com/features/preview/agent-apps-for-github); availability for this operator-owned App must be checked in its settings.

On 19 October 2026 the operator found no agent configuration in this App's settings. GitHub's assignable-actor API returned Copilot, Claude and Codex for both `egil/framework` and this repository, but did not return this App. Early-access onboarding is the next platform prerequisite; adding repository permissions or changing the local CLI cannot be claimed to enable native agent registration.

The documented agent-app runtime uses Copilot cloud agent, with partner functionality through MCP. The documented entry points are issue assignment, pull request comments and the Agents UI. These docs do not establish either a Discussion mention picker or a way to substitute this bridge's existing self-hosted Codex sessions as the runtime. Native agent registration is therefore a separate compatibility investigation; this authentication conversion does not claim to provide it or silently change the runtime.

## Verification

The containerized xUnit suite tests the project-owned authentication/dispatch behavior: valid RSA-signed JWTs, installation/permission validation, repository-scoped tokens, renewal before expiry, unchanged wrapper paths reading renewed tokens, expired/missing cache rejection and no ambient human fallback. It uses controlled HTTP responses and time; this is local adapter evidence, not proof that the real App is installed or has working GitHub permissions.

```sh
docker build -f tests/Containerfile -t discussion-bridge-tests .
docker run --rm discussion-bridge-tests
```

For real compatibility, use the operator's App credentials in a configured container and a distinct human participant. Record the App/bot/installation identity, repository, tool versions, Discussion/source/reply URLs, native session IDs and short redacted log excerpts:

1. Run `--check-auth`; require a confirmed App viewer and repository installation. A missing key, missing installation or insufficient permission is a failed prerequisite, not a passed compatibility check.
2. Mention the actual bot in a disposable post and explicitly enroll another Discussion. Verify bot-authored EYES on the actual source and visible replies in separate native sessions.
3. Add a top-level comment, nested reply and human reaction to bot content. Confirm correct attribution, source receipts, thread routing, own-activity filtering and quiet unchanged polls.
4. Leave the bridge and the same native session running across the refresh window, then request another bot-authored publication. Correlate the refresh log, unchanged session ID and new response. The deterministic suite advances time to exercise this path quickly; it does not replace a real same-session renewal check or one-hour soak.
5. Exercise a renewal failure and verify the native wrapper cannot launch authenticated work or fall through to a participant's credentials. Reconcile any accepted input separately from failed publication/receipt writes. Preserve the prototype's manual uncertainty and no-replay rules.

The complete container bootstrap, configurable additional skills and automated real-provider journey remain tracked in [#5](https://github.com/egil/gh-discussion-agent-bridge/issues/5). This change implements App authentication; it does not provision an App, modify permissions, embed credentials or claim live compatibility without recorded real-provider evidence.
