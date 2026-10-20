# Controller

The controller connects repository Discussions to persistent native coding-agent sessions, publishing as the configured GitHub App bot.

## Language

**Controller enrollment**:
A repository-approved binding of one logical controller device to an immutable GitHub repository, App installation and permission profile. Its private credential permits bounded renewal and self-revocation; it is not the shared App signing key.

**Credential broker**:
The separate trusted service that authorizes enrolled controllers and mints their repository-bound installation tokens. It owns the shared App signing key and never runs an untrusted coding agent.

**Repository approval**:
The repository-held grant for a particular controller enrollment. It is separate from possession of the enrollment's private credential.

**Approval authority**:
A principal GitHub permits to manage the selected repository approval. This authority does not identify a particular human approver.
