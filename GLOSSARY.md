# Controller

The controller connects repository Discussions to persistent native coding-agent sessions, publishing as the configured GitHub App bot.

## Language

**Controller enrollment**:
An administrator-approved binding of one controller device to an immutable GitHub repository, App installation and permission profile. Its secret permits bounded renewal and self-revocation; it is not the shared App signing key.

**Credential broker**:
The separate trusted service that authorizes enrolled controllers and mints their repository-bound installation tokens. It owns the shared App signing key and never runs an untrusted coding agent.

**Approving administrator**:
The person whose repository-administrator authority approved a controller enrollment. That person's continuing authority is required for renewal; the enrollment is separate from their personal GitHub sign-in consent.
