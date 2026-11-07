"""Execute the production cleanup alert query against fixtures in Azure Kusto.

Requires the operator's existing Azure CLI workspace query access. Makes one
read-only query; no telemetry is ingested and no alert notification is sent.
"""
import json
from pathlib import Path
import subprocess


ROOT = Path(__file__).resolve().parents[2]
SUBSCRIPTION = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
WORKSPACE = '66620088-89ad-43a2-b2ce-68abc17af84e'
RESOURCE = '/test/cleanup-insights'
ROLE = 'repoagent-auth-cleanup'
COMPLETED = 'repoagent.auth.cleanup.completed'
FAILURES = 'repoagent.auth.cleanup.failures'


def verification_query():
    query = (ROOT / 'infra/auth/cleanup-observation.kql').read_text()
    if not query.startswith('AppMetrics\n') or query.count('{0}') != 1:
        raise ValueError('Unexpected cleanup observation query source')
    query = query.replace('AppMetrics\n', 'source\n', 1).replace('{0}', RESOURCE)
    rows, expected = [], {}

    def case(name, metrics, alert, *, age=5, resource=RESOURCE, role=ROLE):
        expected[name] = alert
        for metric, minimum, maximum in metrics:
            values = [json.dumps(v) for v in (name, resource, role, metric)]
            rows.append(f'{values[0]}, {age}, {values[1]}, {values[2]}, {values[3]}, real({minimum}), real({maximum})')

    healthy = [(COMPLETED, 1, 1), (FAILURES, 0, 0)]
    incomplete = [(COMPLETED, 0, 0), (FAILURES, 0, 0)]
    error = [(COMPLETED, 1, 1), (FAILURES, 1, 1)]
    case('healthy', healthy, 0)
    case('reported-error', error, 1)
    case('incomplete', incomplete, 1)
    case('healthy-and-incomplete', healthy + incomplete, 1)
    case('incomplete-and-healthy', incomplete + healthy, 1)
    case('healthy-and-error', healthy + error, 1)
    case('aggregated-completion-zero-and-one', [(COMPLETED, 0, 1), (FAILURES, 0, 0)], 1)
    case('missing-completion', [(FAILURES, 0, 0)], 1)
    case('missing-failures', [(COMPLETED, 1, 1)], 1)
    case('no-metrics', [], 1)
    case('stale-metrics', healthy, 1, age=20)
    case('wrong-role', healthy, 1, role='another-service')
    case('wrong-resource', healthy, 1, resource='/another-resource')
    case('expired-incomplete-then-healthy', incomplete, 0, age=20)
    case('expired-incomplete-then-healthy', healthy, 0)

    fixture = ('let fixtures = datatable(Case:string, AgeMinutes:long, _ResourceId:string, '
               'AppRoleName:string, Name:string, Min:real, Max:real)[\n'
               + ',\n'.join(rows) + '\n]\n'
               '| extend TimeGenerated=now() - AgeMinutes * 1m;\n')
    function = ('let check = (source:(Case:string, TimeGenerated:datetime, _ResourceId:string, '
                'AppRoleName:string, Name:string, Min:real, Max:real)) {\n' + query + '\n};\n')
    checks = [f'(fixtures | where Case == {json.dumps(name)} | invoke check() | extend Case={json.dumps(name)})'
              for name in expected]
    return fixture + function + 'union\n' + ',\n'.join(checks) + '\n| project Case, Alert', expected


def main():
    query, expected = verification_query()
    try:
        result = subprocess.run([
            'az', 'monitor', 'log-analytics', 'query', '--subscription', SUBSCRIPTION,
            '--workspace', WORKSPACE, '--analytics-query', query, '-o', 'json'
        ], capture_output=True, text=True, timeout=45)
    except (OSError, subprocess.TimeoutExpired):
        raise SystemExit('UNAVAILABLE: Azure CLI workspace query did not complete') from None
    if result.returncode:
        raise SystemExit('UNAVAILABLE: Azure rejected the fixture query; check CLI sign-in and workspace query access')
    try:
        rows = json.loads(result.stdout)
        actual = {row['Case']: int(row['Alert']) for row in rows}
    except (ValueError, KeyError, TypeError):
        raise SystemExit('UNAVAILABLE: unexpected Azure query response') from None
    print(json.dumps(actual, indent=2, sort_keys=True))
    if len(rows) != len(expected) or actual != expected:
        differences = {name: {'expected': value, 'actual': actual.get(name)}
                       for name, value in expected.items() if actual.get(name) != value}
        raise SystemExit('FAIL: cleanup alert regression ' + json.dumps(differences, sort_keys=True))
    print(f'PASS: {len(expected)} cleanup alert scenarios executed by Azure Kusto')


if __name__ == '__main__':
    main()
