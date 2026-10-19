# Issue tracker: GitHub

Issues and specs live in GitHub Issues for `egil/gh-discussion-agent-bridge`. Use the `gh` CLI from this clone.

## Operations

- Read: `gh issue view <number> --json number,title,body,labels,comments`.
- List: `gh issue list --state open --json number,title,body,labels`, with appropriate label and state filters.
- Create: `gh issue create --title "..." --body-file <file>`.
- Comment: `gh issue comment <number> --body-file <file>`.
- Update labels: `gh issue edit <number> --add-label "..."` or `--remove-label "..."`.
- Close: `gh issue close <number> --comment "..."`.

For multiline bodies, write the exact Markdown to a temporary file and pass `--body-file`.

When a skill says to publish to the issue tracker, create a GitHub issue. When it says to fetch a ticket, read the issue and its comments.

## Parent tickets and dependencies

Use native GitHub sub-issues for parent/child relationships and native issue dependencies for blockers. When unavailable, add `Part of #<parent>` or `Blocked by: #<number>` to the child body and link children in the parent's task list. A ticket is unblocked when all its blockers are closed.

## Pull requests as a triage surface

**PRs as a request surface: no.**

If enabled later, triage PRs using the same roles and the corresponding `gh pr` commands. Issues and PRs share a number space; resolve the type before operating on a bare number.
