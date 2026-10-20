# Controller

The controller connects repository Discussions to persistent native coding-agent sessions, publishing as the configured GitHub App bot.

## Language

**Controller enrollment**:
A binding of one controller device to an immutable GitHub repository, App installation and permission profile, authorized by a repository-held approval. Its secret permits bounded renewal and self-revocation; it is not the shared App signing key.

**Credential broker**:
The separate trusted service that authorizes enrolled controllers and mints their repository-bound installation tokens. It owns the shared App signing key and never runs an untrusted coding agent.

**Repository-held approval**:
The repository-controlled grant authorizing one exact controller enrollment. Its continued presence, unchanged binding and the broker's active enrollment state govern renewal; authorization is not tied to a particular person's continuing administrator role.

**Approval marker**:
The nonsecret, enrollment-specific representation of a repository-held approval. A copied marker identifies an approval but does not authenticate a controller.
