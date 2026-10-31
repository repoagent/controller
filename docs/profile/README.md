# RepoAgent

`@repoagent` is the mention account for [**Repo Agent Connector**](https://github.com/apps/repo-agent-connector), which connects GitHub conversations to agent sessions. This account represents the connector, not a person.

In a repository where **Repo Agent Connector** is installed and set up, mention `@repoagent` in a Discussion to reach the agent. The `repoagent` CLI forwards the conversation to its agent session.

Replies and 👀 receipts are published by the configured **GitHub App**, **Repo Agent Connector**, currently `repo-agent-connector[bot]`. This user account provides the short, recognizable mention handle. A 👀 receipt means the agent accepted the input; it does not mean the work is finished.

The current implementation supports GitHub Discussions. Mentioning this account elsewhere does not automatically start an agent. Issue and pull request support may follow.

Project and documentation: [repoagent/controller](https://github.com/repoagent/controller).

For questions about this account, contact [@egil](https://github.com/egil).
