# Controller

The controller connects repository Discussions to persistent native coding-agent sessions, publishing as the configured GitHub App bot.

## Language

**Controller enrollment**:
A binding of one logical controller device to an immutable GitHub repository, App installation and permission profile, authorized by a repository-held approval. Its secret permits bounded renewal and self-revocation; it is not the shared App signing key.

**Credential broker**:
The separate trusted service that authorizes enrolled controllers and mints their repository-bound installation tokens. It owns the shared App signing key and never runs an untrusted coding agent.

**Repository-held approval**:
The repository-controlled grant authorizing one exact controller enrollment. Its continued presence, unchanged binding and the broker's active enrollment state govern renewal; authorization is not tied to a particular person's continuing administrator role.

**Approval marker**:
The nonsecret, enrollment-specific representation of a repository-held approval. A copied marker identifies an approval but does not authenticate a controller.

**Approval authority**:
A principal GitHub permits to manage the selected repository-held approval. This authority does not identify a particular human approver.

**Repository-settings profile**:
A controller enrollment profile, chosen explicitly at enrollment, whose runtime authority adds read-only access to repository settings. Other enrollment profiles are frozen and never gain it.
_Avoid_: settings upgrade, widened profile

**Repository setting**:
A nonsecret controller setting held in a direct repository Actions variable named `REPOAGENT_*`. Host GitHub authority writes it; the controller's runtime credential may only read it. Absence, an invalid value and a failed read are distinct states.

**Active-session limit**:
The most native Discussion turns one controller runs at the same time, set by the `REPOAGENT_MAX_ACTIVE_SESSIONS` repository setting. Only running turns count; idle stored sessions do not. Input beyond the limit waits in the controller.

**Model label**:
A Discussion label `repoagent:<model>:<reasoning>` that selects the Codex model and reasoning effort for that Discussion's next turn, overriding the repository defaults. It is configuration, not input: changing it never starts or steers a turn.
