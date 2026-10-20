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
