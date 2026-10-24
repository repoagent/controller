# Controller

The controller connects repository Discussions to persistent agent sessions, publishing as the configured GitHub App bot.

## Language

### Controller authority

**Operator**:
The person who installs, enrolls, configures and runs a controller for a repository, using admin authority.
_Avoid_: admin, host, user

**Controller enrollment**:
A binding of one logical controller to an immutable GitHub repository, App installation and permission profile, authorized by a controller approval. Its controller secret permits bounded renewal and self-revocation; it is not the shared App signing key. It is not Discussion enrollment, which starts a Discussion's agent session.
_Avoid_: host enrollment, device enrollment

**Controller secret**:
The secret a controller presents to the credential broker to renew and self-revoke its controller enrollment.
_Avoid_: device secret, enrollment secret

**Enrollment ID**:
The credential broker's identifier for one controller enrollment. A fresh controller enrollment gets a new one.
_Avoid_: device ID, controller ID

**Setup secret**:
The secret proving the setup process owns one pending controller enrollment, used only until the enrollment is acknowledged. It never reaches the controller.
_Avoid_: poll secret, installer secret, enrollment secret

**Credential broker**:
The separate trusted service that authorizes enrolled controllers and mints their GitHub tokens. It owns the shared App signing key and never runs an untrusted coding agent. "Broker" is accepted as the short form.
_Avoid_: auth service, auth broker

**GitHub token**:
The short-lived token the credential broker issues for one controller enrollment. Every GitHub operation of the controller and its agent, including the agent's `gh` and Git calls, acts as the App bot on its one repository through it. Like a workflow's GitHub token, it belongs to the repository and the App, not to a person.
_Avoid_: gh token, installation token, runtime credential, App token

**Controller approval**:
The repository-controlled grant authorizing one exact controller enrollment. Its continued presence, unchanged binding and the broker's active enrollment state govern renewal; authorization is not tied to a particular person's continuing administrator role.
_Avoid_: repository-held approval, approval

**Approval marker**:
The nonsecret, enrollment-specific representation of a controller approval. A copied marker identifies a controller approval but does not authenticate a controller.

**Approval variable**:
The direct repository Actions variable that holds a controller approval's marker, named at controller enrollment.
_Avoid_: approval location

**Approval authority**:
A principal GitHub permits to manage a controller approval. This authority does not identify a particular human approver.

**Admin authority**:
The operator's own GitHub sign-in, used outside the controller for setup work such as writing the controller approval and repository settings. It must be an approval authority to enroll or revoke, and it never reaches the controller.
_Avoid_: host GitHub authority, operator token

**Runtime authority**:
The permissions a GitHub token carries, fixed by its controller enrollment's profile.
_Avoid_: runtime scope, token permissions

**Repository-settings profile**:
A controller enrollment profile, chosen explicitly at controller enrollment, whose runtime authority adds read-only access to direct repository Actions variables, and so to repository settings. Other profiles are frozen and never gain it.
_Avoid_: repository-settings family, settings enrollment, settings upgrade, widened profile

### Agent

**Agent**:
The coding agent a controller runs on behalf of enrolled Discussions, currently Codex.
_Avoid_: native agent, native

**Agent runtime**:
The process a controller starts to host agent sessions and run agent turns, currently `codex app-server`.
_Avoid_: app-server, native runtime

**Agent session**:
The agent's one persistent conversation for an enrolled Discussion.
_Avoid_: native session, native thread, Codex thread

**Agent turn**:
One run of the agent in an agent session, from delivered input until it completes.
_Avoid_: native turn, native work

### Repository configuration

**Repository setting**:
A nonsecret controller setting held in a direct repository Actions variable named `REPOAGENT_*`. Admin authority writes it; the controller's GitHub token may only read it. Absence, an invalid value and a failed read are distinct states.

**Active-session limit**:
The most agent turns one controller runs at the same time, set by the `REPOAGENT_MAX_ACTIVE_SESSIONS` repository setting. Only running turns count; idle stored sessions do not. Input beyond the limit waits in the controller.

**Model label**:
A Discussion label `repoagent:<model>:<reasoning>` that selects the Codex model and reasoning effort for that Discussion's next turn, overriding the repository defaults. It is configuration, not input: changing it never starts or steers a turn.

### Discussion work

**Discussion enrollment**:
Starting a Discussion's one agent session, so that its input reaches the agent. A mention of the App or mention account, an explicit enrollment URL, or category enrollment enrolls a Discussion; labels never do, and the dashboard is never enrolled.
_Avoid_: unqualified "enrollment", Discussion selection

**Category enrollment**:
Discussion enrollment, without a mention, of a Discussion created in a category that the `REPOAGENT_ENROLLMENT_CATEGORIES` repository setting selects. A Discussion created before the controller first saw its category selected is preexisting and never enrolls this way.

**Waiting input**:
Observed Discussion input that the controller has recorded but not yet delivered in an agent turn. It waits in order, without an EYES receipt, while its session is busy or something else, such as the active-session limit, holds it back. It never counts as processed work.

**Redelivery**:
Sending waiting input again, marked as a redelivery, after a delivery attempt that the agent session may have accepted without acknowledging. The agent checks its history before repeating work; duplicate processing is accepted and exactly-once input is not promised.

**EYES receipt**:
The App's EYES reaction on a Discussion, comment or reply, added once the agent session has acknowledged input from that source. It means the input was accepted, not that the answer is finished.

### Operations

**GitHub outbox**:
The controller's durable queue of its own GitHub writes, such as EYES receipts and dashboard updates, retained until each succeeds or fails permanently. It never resends agent input, and agent replies do not pass through it.

**Status endpoint**:
The controller's local `GET /status`, which reports it busy while an agent turn runs or input is pending, retrying or waiting, and idle otherwise, with its work counts. A hosting environment reads it to decide whether it may stop the controller; the controller never acts on it.
_Avoid_: health check

**Dashboard**:
The controller-owned Discussion, in an operator-chosen category, where the controller publishes its nonsecret status, a heartbeat and update notices for newer baseline releases. It is never agent input.

**Baseline release**:
A GitHub Release of `repoagent/controller` that records one anonymously verified public controller image, with its immutable reference and tool versions. Publishing one never changes a running controller; operators adopt it deliberately.
_Avoid_: latest release
