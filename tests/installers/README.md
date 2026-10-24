# Controller process tests

The installer (`install.*`, `repoagent*`, `scripts/*.ps1`) and the container runtime (`container/`) have no automated tests. Per [ADR 0010](../../docs/adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md), they are tested manually against the local AppHost ([#204](https://github.com/repoagent/controller/issues/204), not built yet) when they change.

This directory keeps four controller process suites until [#203](https://github.com/repoagent/controller/issues/203) replaces each one with unit tests:

- `test_managed_git.py`: the managed Git credential helper, through the `CredentialDriver` fixture.
- `test_checkout_refresh.py`: the managed checkout refresh, waiting input and redelivery, signed webhook deliveries and deleted Discussions.
- `test_checkout_native.py`: the agent runtime's view of the trusted checkout. The controller xUnit tests also call it with `--observe-production`.
- `test_controller_dashboard.py`: the dashboard heartbeat and stop reports.

`scripts/delivery/verify.sh` runs them on Linux with the pinned .NET SDK and network access to download the pinned Codex:

```sh
python3 -m unittest discover -s tests/installers -p 'test_*.py'
```
