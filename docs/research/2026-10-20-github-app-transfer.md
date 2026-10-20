# GitHub App ownership transfer diagnosis

Checked 2026-10-20 against official GitHub documentation. This note investigates a reported transfer failure for `repo-agent-connector` after the controller repository moved to `repoagent/controller`. The investigation did not alter external settings; the operator changed the app's visibility in GitHub. Public documentation does not identify the exact server-side rule behind the reported error.

## Operator-observed cause and workaround

The operator confirmed that this app was private, changed it to public, and then reported: “it was private, now its public, transfering it now”. This identifies app visibility as the observed blocker/workaround for this particular transfer attempt. It does **not** establish a universal rule that all private GitHub Apps must become public before ownership can transfer.

The destination account `repoagent` was independently verified through GitHub's API as type `User`. Before the visibility change, fetching `apps/repo-agent-connector` with the current user's OAuth authentication returned HTTP 404. That 404 alone does not distinguish a private registration from a missing or inaccessible one, so it was not sufficient evidence of the cause.

## Verified completion

After the operator's transfer, this read-only command succeeded:

```sh
gh api apps/repo-agent-connector \
  --jq '{id,slug,owner:{login:.owner.login,type:.owner.type},html_url,external_url}'
```

It returned App ID `5387625`, slug `repo-agent-connector`, owner `repoagent` of type `User`, public page `https://github.com/apps/repo-agent-connector` and homepage `https://github.com/repoagent/controller`. The ID matches the original registration documented in this repository, so this verifies transfer of the existing App rather than creation of a replacement. [Public App metadata](https://api.github.com/apps/repo-agent-connector).

## Confirmed transfer rules

GitHub explicitly permits transferring a GitHub App registration to another **user or organization**. The app owner or an app manager can initiate it; teams cannot receive ownership. A destination being a personal account is therefore insufficient to explain a failure. The documented flow is **Developer settings → GitHub Apps → app → Advanced → Transfer ownership**; type the destination account name, select the actual account from the dropdown, then submit. GitHub may warn when a transfer would remove an installation. [Transfer documentation](https://docs.github.com/en/apps/maintaining-github-apps/transferring-ownership-of-a-github-app).

For enterprise destinations, GitHub documents additional conditions: a private/internal app can transfer to an enterprise that its owner belongs to; it becomes internal and loses user-account installations. This enterprise-specific rule does not establish a restriction on transfers between ordinary personal accounts. [Enterprise transfer documentation](https://docs.github.com/en/enterprise-cloud%40latest/apps/maintaining-github-apps/transferring-ownership-of-a-github-app).

## Separate concepts that do not prove the cause

- **App ownership versus repository access:** the app-manager role controls registration settings and does not itself allow organization installations. Repository collaborator access does not establish app ownership. [App managers](https://docs.github.com/en/apps/maintaining-github-apps/about-github-app-managers).
- **Public app versus public repository:** a public app can be installed by other accounts; a private app can only be installed on its registration owner's account. Public availability is separate from an optional Marketplace listing. The docs do not state that public apps are generally prohibited from transferring to personal accounts. [App visibility](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/making-a-github-app-public-or-private).
- **Recipient acceptance:** the GitHub App transfer page does not document a recipient-acceptance or prior-installation requirement. OAuth app transfers do explicitly require acceptance from a pending-transfer list; that is a different app type. The absence of a GitHub App requirement in the cited page is not proof that GitHub has no additional server-side validation. [GitHub App transfer](https://docs.github.com/en/apps/maintaining-github-apps/transferring-ownership-of-a-github-app), [OAuth app transfer](https://docs.github.com/en/apps/oauth-apps/maintaining-oauth-apps/transferring-ownership-of-an-oauth-app).
- **Enterprise Managed Users:** these accounts have separate app and repository restrictions. Enterprise-owned and managed-user-owned apps have internal visibility, and managed users generally cannot install apps on their personal accounts. These restrictions are relevant only if the actual accounts are managed enterprise accounts. [Managed-user restrictions](https://docs.github.com/en/enterprise-cloud%40latest/admin/managing-iam/understanding-iam-for-enterprises/abilities-and-restrictions-of-managed-user-accounts), [Enterprise app visibility](https://docs.github.com/en/enterprise-cloud%40latest/apps/creating-github-apps/registering-a-github-app/making-a-github-app-public-or-private).
- **Paid Marketplace listing:** a paid app must belong to a verified organization publisher. That is a real ownership constraint for paid listings, but does not explain an ordinary public app merely because it is publicly installable. [Marketplace listing requirements](https://docs.github.com/en/apps/github-marketplace/creating-apps-for-github-marketplace/requirements-for-listing-an-app).

## Limits of the diagnosis

Searches for the reported wording and close variants did not locate an official error reference. The operator's visibility change and subsequent transfer attempt provide the practical explanation for this incident; the exact GitHub validation rule remains undocumented in the sources found.

The live owner and stable-ID check confirmed successful completion. No further reproduction or error collection is needed for this resolved incident.
