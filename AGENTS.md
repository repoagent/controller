## Agent skills

### Issue tracker

Track issues and specs in GitHub Issues for `repoagent/controller`. Before reading or updating tickets, read `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default triage labels. Before triaging issues, read `docs/agents/triage-labels.md`.

### Domain docs

Use a single-context layout: root `GLOSSARY.md` and `docs/adr/`. Before exploring domain concepts or decisions, read `docs/agents/domain.md`.

### Azure deployment

Before changing auth-service Azure resources, deployment workflows, or GitHub OIDC trust, read `docs/azure-auth-deployment.md`.

## README maintenance

Update `README.md` in the same change whenever supported features, installation requirements, configuration, or user-visible behavior and limitations change. Its audience is end users and repository admins installing and using RepoAgent in their own repositories. Keep it concise: lead with a bullet list of currently supported features, followed by getting-started and everyday-use guidance. State prerequisites and optional features clearly. Keep implementation details in `docs/` or relevant ADRs, linked from the README.
