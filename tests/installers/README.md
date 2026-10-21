# Installer process tests

Run `python -m unittest discover -s tests/installers -v` with PowerShell 7 available.

Windows host-enrollment TLS tests temporarily install each journey's freshly
created **public localhost certificate** into the ordinary LocalMachine Root
store. Run from an **already administrative** shell and explicitly opt in:

```powershell
$env:REPOAGENT_TEST_WINDOWS_ROOT = 'LocalMachine'
python -m unittest discover -s tests/installers -p test_host_enrollment.py -f -v
```

The helper checks existing rights; it never elevates or changes UAC or machine
policy. Missing opt-in or administrator rights produces a setup error, not a
skipped test. Add and removal run in separate processes, each bounded at15 seconds.
Cleanup removes only this journey's matching thumbprint **and exact DER bytes**,
then reopens the normal store to verify absence. A killed parent/job can prevent
cleanup; use disposable CI runners for unattended execution.

This test-fixture permission requirement does not change production installer
permissions. The production HTTPS client retains normal certificate validation.
The private key remains in the temporary test directory and is never imported.

The Windows certificate-trust job also runs `test_guided_install.py` with native
GitHub/Docker executable transport fixtures. Guided private state uses protected
current-user DACLs under LOCALAPPDATA, exercising the actual private-file reader.
This process coverage does not establish Docker Desktop or real Codex acceptance.

The opt-in `test_guided_install_assembled.py` enters the public installer and owns
an isolated production broker/Table/Azurite/DataProtection fixture and real Linux
Docker runtime. Supply `REPOAGENT_GUIDED_ASSEMBLED=1` and
`REPOAGENT_TEST_NATIVE_CONTROLLER` pointing to the reviewed native artifact. The
test checks its hash and source baseline before use. It currently requires the
recorded issue73 immutable cached runtime base; its image contains current runtime
source/native binary, a temporary public test CA and controlled external gh/Codex
transports. Private CA keys and host credentials stay outside the image. On Docker
Desktop, `host.docker.internal` provides the normal TLS SAN/origin; another host
may supply a reachable normal DNS name with `REPOAGENT_GUIDED_BROKER_HOST`.

A real quota denial permits only the explicitly named same-instance resume phases:
one measured repository-window wait, at most one more initial runtime-auth window,
and one separately bounded restart-auth window. Identity, marker, private receipt
and immutable image must survive. There is no blanket test retry, quota increase,
TLS bypass or live provider access. The test cleans only its generated containers,
volumes, table and image tags. Native Windows Desktop, real authenticated Codex and
live GitHub authority remain separate acceptance gates.

`test_local_lifecycle.py` covers public preview/selection failures, legacy Bash
refusal and credential-free Revoke. Pre-existing managed process journeys explicitly
assume an absent marker through a fixed read-only Docker inspect response; they do
not prove helper parsing, disable enforcement or cleanup. Other lifecycle helper
operations cannot succeed through that fixture shortcut. Legacy direct Bash
journeys explicitly supply Docker's rendered unlabeled-owner sentinel.

Run `test_local_lifecycle_assembled.py` with
`REPOAGENT_LOCAL_LIFECYCLE_IMAGE` set to an existing local immutable controller
image whose runtime hash matches the source. It derives a minimal network-disabled
fixture layer that replaces only start with credential-free readiness/wait; real
configure/import/get and the unchanged host-carried helper use actual Docker and
UID1000 volumes. Generated resources are removed even on assertion failure. This
proves local lifecycle behavior with a controlled runtime, not native-controller,
provider or Windows Docker Desktop acceptance. No image is pulled or published.
