# Controller authentication without distributing the App key

Checked 2026-10-20 against official GitHub documentation. This is an authentication design investigation, not an implemented feature or a change to external configuration. Protocol recommendations below are proposed controller behavior.

## Documented GitHub capabilities

**A CLI can sign a user into a GitHub App without its PEM or client secret.** Enable device flow on the registration, ship its public client ID, request a device code, and poll for a user access token after browser approval. The documented token request uses `client_id`, `device_code`, and the device grant type, with optional `repository_id` restriction. GitHub's own CLI tutorial embeds only the client ID. [Device flow](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app#using-the-device-flow-to-generate-a-user-access-token), [CLI tutorial](https://docs.github.com/en/apps/creating-github-apps/writing-code-for-a-github-app/building-a-cli-with-a-github-app).

**That token acts as the signed-in user.** Comments and other activity are attributed to that user with App attribution; an installation token instead attributes activity to the App. User-token access is the intersection of the user's access, the App's permissions, and the accounts/repositories where the App is installed. Each user authorizes independently. Authorization alone does not install the App. [User authentication and attribution](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-with-a-github-app-on-behalf-of-a-user), [Authentication modes](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/about-authentication-with-a-github-app).

**Device-token refresh also works without a client secret.** Expiring user tokens last eight hours; refresh tokens last six months and rotate on use, invalidating the previous pair. The refresh endpoint explicitly exempts device-flow tokens from its client-secret requirement. Requesting `offline_access` forces expiring tokens even when registration defaults differ. This scope does not grant permissions or select repositories. Local tokens and refresh tokens still require protected storage. [Refresh documentation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/refreshing-user-access-tokens).

**Signing in does not give the client an installation token.** The installation-token creation endpoint requires an App JWT and explicitly rejects GitHub App user tokens. GitHub documents signing that JWT with the App's private key. Installation tokens can be narrowed to `repository_ids` and permissions, expire after one hour, and cannot exceed the installation's grants. [Installation-token endpoint](https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app), [JWT signing](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app), [Token generation and lifetime](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app).

## Repository selection is a separate authorization step

An owner/admin installs the App into the repository's owning account, selecting the intended repositories. Organization installation policy can require an owner. A normal user's login cannot grant the App access to an uninstalled repository. [Installation flow and repository selection](https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party).

The controller can discover authorized installations with `GET /user/installations`, then list repositories and the user's permission map with `GET /user/installations/{installation_id}/repositories`. This supports a picker containing only eligible repositories. [Installation discovery API](https://docs.github.com/en/rest/apps/installations#list-app-installations-accessible-to-the-user-access-token), [Repository discovery API](https://docs.github.com/en/rest/apps/installations#list-repositories-accessible-to-the-user-access-token).

Selecting a repository in local configuration alone does not reduce GitHub authority. GitHub documents `repository_id` on the device-flow token exchange to restrict a token to one repository, but ignores it if the App or user cannot access that repository. Verify actual token scope rather than treating successful exchange as proof of restriction; preservation through refresh needs compatibility evidence. Without that restriction, a user token may cover the user/App intersection. OAuth scopes are not a per-repository allowlist. [User-access intersection](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-with-a-github-app-on-behalf-of-a-user), [Device-flow parameters](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app#using-the-device-flow-to-generate-a-user-access-token).

GitHub also offers `POST /applications/{client_id}/token/scoped` to derive a user token restricted to repositories and permissions. It requires the App's **client secret** through HTTP Basic authentication, making it suitable for a server holding that secret rather than a distributed public client. It remains a user token with user attribution. The web authorization-code flow supports PKCE, but its documented exchange still requires `client_secret`; PKCE alone is not a verified replacement for device flow in a secret-free native client. [Scoped-token endpoint](https://docs.github.com/en/rest/apps/apps#create-a-scoped-access-token), [Web-flow exchange parameters](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app#using-the-web-application-flow-to-generate-a-user-access-token).

## Options and recommendation

| Approach | Local credential | GitHub actor | Main tradeoff |
| --- | --- | --- | --- |
| Shared App with a hosted token broker | Enrolled-device credential plus repository-limited installation tokens | Shared App bot | Preserves bot identity; requires operating an authorization service. |
| Shared App with direct device login | User access/refresh token | Signed-in user with App attribution | No hosted key service; human actor, with documented repository restriction that must be verified including refresh. |
| User/organization creates its own App | That installation owner's own App key | Their own App bot | No shared-service dependency; more registration work and locally accessible owner key. |

These tradeoffs are architectural conclusions from the documented authentication modes, not additional GitHub features. For an unattended controller expected to speak as the shared bot, use the broker. Direct device login is a useful separate mode when user attribution is acceptable. Keep the own-App option for independent deployments.

Proposed broker protocol:

1. An owner installs the public App on selected repositories. Keep the App PEM in the hosted service; never send it to a controller.
2. The user signs in and enrolls a specific controller device. The service verifies the GitHub user and repository permissions and applies an explicit enrollment rule, such as repository-admin approval. Read access alone must not qualify someone to obtain a bot token with write access.
3. Bind enrollment to immutable repository ID, installation ID, device identity, and an approved permission ceiling. Do not trust repository or installation identifiers merely supplied by the client.
4. On issuance and renewal, authenticate the device and recheck installation grants, enrollment validity, and the responsible user's current authorization. Mint an installation token for exactly the enrolled repository and approved permissions. Send the token and its expiry, never a JWT or PEM.
5. Stop renewal when enrollment, user access, or installation permission is revoked. Already-issued bearer tokens may remain usable until expiration unless individually revoked. Record issuance and renewal metadata without logging credentials. GitHub provides an endpoint to revoke the installation token used for that request. [Installation token revocation](https://docs.github.com/en/rest/apps/installations#revoke-an-installation-access-token).

The broker must perform these authorization checks itself: installation tokens do not inherit the enrolling user's access restrictions. Repository scoping limits their bearer capability, while device authentication controls renewal. A server-side scoped-user-token design is another option when user identity should be preserved; it is not interchangeable with installation-token bot identity.

## Existing implementation boundary

The parent investigation confirmed that the controller already requests repository-scoped installation tokens and renews them. Its wrapper can consume a renewed credential cache, but the application's `Program`/GitHub integration requires concrete PEM-based `GitHubAppCredentials` and expects a bot viewer. Direct user authentication is not implemented; adopting it also requires changing the actor filter that prevents response loops. These observations identify integration work; neither proposed mode is an existing turnkey option.

The read-only PEM mount is readable by the same container UID used by Codex. Read-only prevents modification, not reading or using the key to bypass local scoping policy. This is repository-specific evidence reported by the parallel code inspection, not an independent claim from GitHub documentation. Moving token minting behind a broker removes that shared signing credential from the local runtime; it does not make the issued bearer token inaccessible to that runtime.

## Independent review correction

The initial investigation incorrectly said device flow had no documented repository selector. Independent review corrected that factual claim before implementation. The broker is chosen for shared bot identity and centralized authorization, not because GitHub requires a broker for native device login. See [consumer protocol and release gates](../broker-protocol.md).
