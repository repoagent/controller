# GhDiscussionAgentBridge

Use GitHub Discussions as the conversation interface for a native coding-agent session. A small bridge forwards observed activity; the agent replies through normal tools under a dedicated GitHub identity.

## Start here

- [Minimal PoC — issue #2](https://github.com/egil/gh-discussion-agent-bridge/issues/2): the implementation starting point, with manual verification.
- [Full specification — issue #1](https://github.com/egil/gh-discussion-agent-bridge/issues/1): broader design reference. The PoC's smaller scope takes precedence for the initial build.
- [Flows, payloads and session instructions](https://github.com/egil/gh-discussion-agent-bridge/issues/2#issuecomment-6276915447).

The PoC uses one native Codex session per Discussion, a small JSON mapping/checkpoint, GraphQL polling, an ordinary skill plus `gh` for replies, and timestamped stdout logging. After the native harness accepts input, the bridge adds 👀 to the forwarded source Discussion, comment or reply.

The bridge can monitor configured repositories; it is independent of any particular product. Specifications and tickets live in this repository's GitHub Issues. No bridge implementation has been added yet.
