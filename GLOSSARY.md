# Controller

The controller connects repository Discussions to persistent native coding-agent sessions, publishing as the configured GitHub App bot.

## Language

### Controller authority

**Controller enrollment**:
A binding of one logical controller device to an immutable GitHub repository, App installation and permission profile, authorized by a repository-held approval. Its secret permits bounded renewal and self-revocation; it is not the shared App signing key. It is not Discussion enrollment, which starts a Discussion's native session.

**Credential broker**:
The separate trusted service that authorizes enrolled controllers and mints their repository-bound installation tokens. It owns the shared App signing key and never runs an untrusted coding agent.

**Repository-held approval**:
The repository-controlled grant authorizing one exact controller enrollment. Its continued presence, unchanged binding and the broker's active enrollment state govern renewal; authorization is not tied to a particular person's continuing administrator role.

**Approval marker**:
The nonsecret, enrollment-specific representation of a repository-held approval. A copied marker identifies an approval but does not authenticate a controller.

**Approval authority**:
A principal GitHub permits to manage the selected repository-held approval. This authority does not identify a particular human approver.

**Repository-settings profile**:
A controller enrollment profile, chosen explicitly at controller enrollment, whose runtime authority adds read-only access to direct repository Actions variables, and so to repository settings. Other profiles are frozen and never gain it.
_Avoid_: repository-settings family, settings enrollment, settings upgrade, widened profile

### Repository configuration

**Repository setting**:
A nonsecret controller setting held in a direct repository Actions variable named `REPOAGENT_*`. Host GitHub authority writes it; the controller's runtime credential may only read it. Absence, an invalid value and a failed read are distinct states.

**Active-session limit**:
The most native Discussion turns one controller runs at the same time, set by the `REPOAGENT_MAX_ACTIVE_SESSIONS` repository setting. Only running turns count; idle stored sessions do not. Input beyond the limit waits in the controller.

**Model label**:
A Discussion label `repoagent:<model>:<reasoning>` that selects the Codex model and reasoning effort for that Discussion's next turn, overriding the repository defaults. It is configuration, not input: changing it never starts or steers a turn.

### Discussion work

**Discussion enrollment**:
Starting a Discussion's one native session, so that its input reaches the agent. A mention of the App or mention account, an explicit enrollment URL, or category enrollment enrolls a Discussion; labels never do, and the dashboard is never enrolled.
_Avoid_: unqualified "enrollment", Discussion selection

**Category enrollment**:
Discussion enrollment, without a mention, of a Discussion created in a category that the `REPOAGENT_ENROLLMENT_CATEGORIES` repository setting selects. A Discussion created before the controller first saw its category selected is preexisting and never enrolls this way.

**Waiting input**:
Observed Discussion input that the controller has recorded but not yet delivered in a native turn. It waits in order, without an EYES receipt, while its session is busy or something else, such as the active-session limit, holds it back. It never counts as processed work.

**Redelivery**:
Sending waiting input again, marked as a redelivery, after a delivery attempt that the native session may have accepted without acknowledging. The agent checks its history before repeating work; duplicate processing is accepted and exactly-once input is not promised.

**EYES receipt**:
The App's EYES reaction on a Discussion, comment or reply, added once the native session has acknowledged input from that source. It means the input was accepted, not that the answer is finished.

### Operations

**GitHub outbox**:
The controller's durable queue of its own GitHub writes, such as EYES receipts and dashboard updates, retained until each succeeds or fails permanently. It never resends agent input, and agent replies do not pass through it.

**Dashboard**:
The controller-owned Discussion, in an operator-chosen category, where the controller publishes its nonsecret status, a heartbeat and update notices for newer baseline releases. It is never agent input.

**Baseline release**:
A GitHub Release of `repoagent/controller` that records one anonymously verified public controller image, with its immutable reference and tool versions. Publishing one never changes a running controller; operators adopt it deliberately.
_Avoid_: latest release
