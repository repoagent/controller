#!/usr/bin/env python3
"""Fixed-target, read-only Azure evidence. This never establishes capability settlement."""
import datetime as dt
import json
import math
import os
from pathlib import Path
import re
import selectors
import signal
import subprocess
import sys
import time
from urllib.parse import urlencode

SUBSCRIPTION = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
TENANT = '3c356090-bfe9-459b-a2ac-064231707881'
RESOURCE_GROUP = 'rg-repoagent-controller-auth'
GROUP = f'/subscriptions/{SUBSCRIPTION}/resourceGroups/{RESOURCE_GROUP}'
APP = GROUP + '/providers/Microsoft.App/containerApps/repoagent-auth'
JOB = GROUP + '/providers/Microsoft.App/jobs/repoagent-auth-cleanup'
INSIGHTS = GROUP + '/providers/Microsoft.Insights/components/repoagent-auth-insights'
METRICS = ('repoagent.auth.cleanup.completed', 'repoagent.auth.cleanup.failures',
           'repoagent.auth.cleanup.unknown_deadlines', 'repoagent.auth.cleanup.cursor_age_max_seconds',
           'repoagent.auth.cleanup.cycle_age_max_seconds', 'repoagent.auth.cleanup.cycle_age_unknown',
           'repoagent.auth.cleanup.overdue_payload_age_max_seconds',
           'repoagent.auth.cleanup.stale_cursor_with_overdue_observation', 'repoagent.broker.rejections')
REASONS = ('request_concurrency', 'request_minute', 'repository_attempts', 'global_attempts',
           'retained_issuance', 'repository_minute_budget', 'global_minute_budget', 'retained_enrollments',
           'candidate_repositories', 'repository_pending', 'global_pending', 'enrollment_backoff', 'other')


class Refused(Exception):
    pass


class Unavailable(Exception):
    pass


def command(arguments):
    # Stream limits apply while reading, not after an unbounded communicate/capture_output.
    environment = dict(os.environ, AZURE_CORE_COLLECT_TELEMETRY='0', AZURE_EXTENSION_USE_DYNAMIC_INSTALL='no')
    process = subprocess.Popen(arguments, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.DEVNULL, start_new_session=True, env=environment)
    deadline, output = time.monotonic() + 30, bytearray()
    try:
        with selectors.DefaultSelector() as reader:
            reader.register(process.stdout, selectors.EVENT_READ)
            while reader.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise Unavailable('command-timeout')
                for key, _ in reader.select(remaining):
                    chunk = os.read(key.fileobj.fileno(), 8192)
                    if not chunk:
                        reader.unregister(key.fileobj)
                        continue
                    output.extend(chunk)
                    if len(output) > 2 * 1024 * 1024:
                        raise Unavailable('response-limit')
        if process.wait(timeout=max(0.01, deadline-time.monotonic())):
            raise Unavailable('provider-command-failed')
        return output.decode('utf-8', errors='strict')
    except (OSError, UnicodeError, subprocess.TimeoutExpired):
        raise Unavailable('provider-command-failed') from None
    finally:
        if process.poll() is None:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
        process.stdout.close()


def document(arguments):
    try:
        return json.loads(command(arguments))
    except (ValueError, TypeError):
        raise Unavailable('invalid-response') from None


def read(path, version='2025-01-01', **parameters):
    # No provider nextLink or caller URL is ever followed.
    url = 'https://management.azure.com' + path + '?' + urlencode({'api-version': version, **parameters})
    return document(['az', 'rest', '--method', 'get', '--url', url, '--only-show-errors', '--output', 'json'])


def checked_source():
    expected = os.environ.get('AUTH_EXPECTED_SOURCE_SHA', '')
    required = dict(GITHUB_REPOSITORY='repoagent/controller', GITHUB_REPOSITORY_ID='1426154477',
                    GITHUB_REPOSITORY_OWNER_ID='345029828', GITHUB_REF='refs/heads/main',
                    GITHUB_EVENT_NAME='workflow_dispatch', AZURE_SUBSCRIPTION_ID=SUBSCRIPTION,
                    AZURE_TENANT_ID=TENANT, AZURE_RESOURCE_GROUP=RESOURCE_GROUP, AZURE_LOCATION='swedencentral')
    if os.name != 'posix' or any(os.environ.get(key) != value for key, value in required.items()) \
            or not re.fullmatch('[0-9a-f]{40}', expected) or os.environ.get('GITHUB_SHA') != expected:
        raise Refused('Use the exact accepted main workflow and fixed RepoAgent target on Linux')
    if os.environ.get('AUTH_APP_NAME') not in (None, '', 'repoagent-auth'):
        raise Refused('The configured application differs from the fixed diagnostics target')
    if command(['git', 'remote', 'get-url', 'origin']).strip() not in (
            'https://github.com/repoagent/controller.git', 'https://github.com/repoagent/controller',
            'git@github.com:repoagent/controller.git'):
        raise Refused('Unverified source repository')
    if command(['git', 'rev-parse', 'HEAD']).strip() != expected or command(
            ['git', 'ls-remote', '--exit-code', 'origin', 'refs/heads/main']).split() != [expected, 'refs/heads/main']:
        raise Refused('Accepted source, checkout and current main must agree')
    account = document(['az', 'account', 'show', '--only-show-errors', '--output', 'json'])
    if account.get('id') != SUBSCRIPTION or account.get('tenantId') != TENANT:
        raise Refused('Azure account differs from the fixed diagnostics subscription and tenant')
    group = read(GROUP, '2024-03-01')
    if group.get('id', '').lower() != GROUP.lower() or group.get('location') != 'swedencentral':
        raise Refused('Azure resource group differs from the fixed diagnostics target')
    return expected


def number(value, maximum=10**15):
    if value is None:
        return None
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) \
            or value < 0 or value > maximum:
        raise Unavailable('invalid-response')
    return value


def timestamp(value):
    if value is None:
        return None
    parsed = dt.datetime.fromisoformat(value.replace('Z', '+00:00'))
    if parsed.tzinfo is None:
        raise Unavailable('invalid-response')
    return parsed.astimezone(dt.timezone.utc).isoformat().replace('+00:00', 'Z')


def choice(value, allowed):
    return value if value in allowed else 'Unknown'


def images(template):
    values = template.get('containers', [])
    if not isinstance(values, list) or len(values) > 4:
        raise Unavailable('invalid-response')
    pattern = r'ghcr\.io/repoagent/controller-auth(?:@sha256:[0-9a-f]{64}|:broker-v[0-9]+\.[0-9]+\.[0-9]+|:sha-[0-9a-f]{40}-[0-9]+-[0-9]+)'
    return sorted({value['image'] if re.fullmatch(pattern, value.get('image', '')) else 'unrecognized' for value in values})


def resource(path):
    value = read(path)
    if value.get('id', '').lower() != path.lower() or any(value.get('tags', {}).get(key) != expected for key, expected in
            dict(repositoryId='1426154477', ownerId='345029828', managedBy='repoagent-auth').items()):
        raise Unavailable('target-ownership-mismatch')
    properties = value['properties']
    result = dict(images=images(properties.get('template', {})), provisioning=choice(properties.get('provisioningState'),
                 ('Succeeded', 'Failed', 'Updating', 'Canceled', 'Provisioning')))
    config = properties.get('configuration', {})
    if path == JOB:
        schedule = config.get('scheduleTriggerConfig', {})
        cron = schedule.get('cronExpression', '')
        result.update(trigger=choice(config.get('triggerType'), ('Schedule', 'Manual', 'Event')),
                      timeoutSeconds=number(config.get('replicaTimeout'), 86400),
                      retries=number(config.get('replicaRetryLimit'), 100),
                      cron=cron if re.fullmatch(r'[0-9*/ ,\-]{1,64}', cron) else 'unrecognized',
                      parallelism=number(schedule.get('parallelism'), 100),
                      completions=number(schedule.get('replicaCompletionCount'), 100))
    else:
        scale = properties.get('template', {}).get('scale', {})
        revision = properties.get('latestReadyRevisionName', '')
        result.update(revision=revision if re.fullmatch('repoagent-auth--[a-z0-9-]{1,64}', revision) else 'unrecognized',
                      revisionMode=choice(config.get('activeRevisionsMode'), ('Single', 'Multiple', 'Labels')),
                      minReplicas=number(scale.get('minReplicas'), 1000), maxReplicas=number(scale.get('maxReplicas'), 1000))
    return result


def collection(value, maximum=100):
    items = value['value']
    if not isinstance(items, list) or len(items) > maximum:
        raise Unavailable('response-limit')
    return items


def capture(operation):
    try:
        return dict(state='collected', observations=operation())
    except (Unavailable, ValueError, TypeError, KeyError, AttributeError) as error:
        return dict(state='unavailable', reason=str(error) if isinstance(error, Unavailable) else 'invalid-response')


def executions():
    response = read(JOB + '/executions')
    rows = []
    for item in collection(response):
        name = item['name']
        if not re.fullmatch('repoagent-auth-cleanup-[a-z0-9-]{1,80}', name):
            raise Unavailable('invalid-response')
        value = item['properties']
        rows.append(dict(name=name, start=timestamp(value.get('startTime')), end=timestamp(value.get('endTime')),
                         status=choice(value.get('status'), ('Running', 'Processing', 'Stopped', 'Degraded', 'Failed', 'Succeeded')),
                         images=images(value.get('template', {}))))
    rows.sort(key=lambda item: item['start'] or '', reverse=True)
    selected = rows[:20]
    def replicas(name):
        # The CLI's replica command uses this separate preview API, not Jobs' stable version.
        value = read(JOB + '/executions/' + name + '/replicas', '2023-11-02-preview')
        items = collection(value, 64)
        if value.get('nextLink'):
            raise Unavailable('response-limit')
        states = []
        for item in items:
            for container in item.get('properties', {}).get('containers', []):
                ready = container.get('ready')
                states.append(dict(ready=ready if isinstance(ready, bool) else None,
                    restarts=number(container.get('restartCount')),
                    state=choice(container.get('runningState'), ('Running', 'NotRunning', 'Waiting', 'Terminated', 'Unknown'))))
                if len(states) > 64:
                    raise Unavailable('response-limit')
        return dict(count=len(items), containers=states)
    return dict(items=selected, truncated=bool(response.get('nextLink')) or len(rows) > 20,
                coverage='Newest 20 from one returned page; replicas for newest three only. Not a drain fence or full history.',
                replicas=[dict(execution=row['name'], **capture(lambda name=row['name']: replicas(name))) for row in selected[:3]])


def alerts():
    response = read(GROUP + '/providers/Microsoft.AlertsManagement/alerts', '2019-03-01',
                    targetResource=JOB, alertRule='repoagent-auth-cleanup-execution-failed', includeContext='false',
                    includeEgressConfig='false', timeRange='1d', pageCount='10', sortOrder='desc')
    result = []
    if response.get('nextLink'):
        raise Unavailable('response-limit')
    for item in collection(response, 10):
        value = item['properties']['essentials']
        if value.get('targetResource', '').lower() != JOB.lower() or value.get('alertRule') != 'repoagent-auth-cleanup-execution-failed':
            raise Unavailable('target-ownership-mismatch')
        result.append(dict(condition=choice(value.get('monitorCondition'), ('Fired', 'Resolved')),
                           severity=choice(value.get('severity'), ('Sev0', 'Sev1', 'Sev2', 'Sev3', 'Sev4')),
                           started=timestamp(value.get('startDateTime')), modified=timestamp(value.get('lastModifiedDateTime'))))
    return result


def metrics():
    workspace_path = GROUP + '/providers/Microsoft.OperationalInsights/workspaces/repoagent-auth-logs'
    workspace = read(workspace_path, '2023-09-01')
    if workspace.get('id', '').lower() != workspace_path.lower():
        raise Unavailable('target-ownership-mismatch')
    workspace_id = workspace['properties']['customerId']
    if not re.fullmatch('[0-9a-fA-F-]{36}', workspace_id):
        raise Unavailable('invalid-response')
    names = ','.join("'" + metric + "'" for metric in METRICS)
    query = f"""AppMetrics
| where TimeGenerated >= ago(24h) and _ResourceId =~ '{INSIGHTS}'
| where Name in ({names})
| extend Reason=iff(Name == 'repoagent.broker.rejections', tostring(Properties['reason']), '')
| summarize Samples=count(), LastObserved=max(TimeGenerated), Total=sum(Sum), Maximum=max(Max) by Name, Reason
| take 64"""
    url = 'https://api.loganalytics.azure.com/v1/workspaces/' + workspace_id + '/query?' + urlencode(dict(query=query, timespan='P1D'))
    response = document(['az', 'rest', '--method', 'get', '--url', url, '--resource', 'https://api.loganalytics.io',
                         '--only-show-errors', '--output', 'json'])
    if response.get('error') or len(response['tables']) != 1:
        raise Unavailable('invalid-response')
    table = response['tables'][0]
    columns = [column['name'] for column in table['columns']]
    expected = ('Name', 'Reason', 'Samples', 'LastObserved', 'Total', 'Maximum')
    if len(columns) != 6 or set(columns) != set(expected) or len(table['rows']) >= 64:
        raise Unavailable('response-limit')
    result = []
    for row in table['rows']:
        if len(row) != 6:
            raise Unavailable('invalid-response')
        value = dict(zip(columns, row))
        if value['Name'] not in METRICS or value['Reason'] not in ('', *REASONS):
            raise Unavailable('invalid-response')
        result.append(dict(metric=value['Name'], reason=value['Reason'], samples=number(value['Samples']),
                           lastObserved=timestamp(value['LastObserved']), total=number(value['Total']), maximum=number(value['Maximum'])))
    return result


def collect(output):
    source = checked_source()
    if not output.is_absolute() or output.exists():
        raise Refused('Use a new absolute evidence directory')
    output.mkdir(mode=0o700, parents=True)
    report = dict(schema='repoagent.broker-diagnostics/v1', source=source,
                  observedAt=dt.datetime.now(dt.timezone.utc).isoformat(),
                  target=dict(subscription=SUBSCRIPTION, resourceGroup=RESOURCE_GROUP), settlementProven=False,
                  limits=dict(commandSeconds=30, responseBytes=2*1024*1024, executionRows=20, replicaExecutions=3,
                              alerts=10, metricsRows=63, metricsWindow='P1D'),
                  limitations='Read-only observations do not identify private retained authority, prove settlement, authorize replacement, or establish restore/notification acceptance.')
    for name, operation in (('api', lambda: resource(APP)), ('cleanup', lambda: resource(JOB)),
                            ('executions', executions), ('alerts', alerts), ('metrics', metrics)):
        report[name] = capture(operation)
    unavailable = any(report[name]['state'] != 'collected' for name in ('api','cleanup','executions','alerts','metrics'))
    if report['executions']['state'] == 'collected':
        unavailable |= any(item['state'] != 'collected' for item in report['executions']['observations']['replicas'])
    report['state'] = 'partial' if unavailable else 'collected'
    path = output / 'diagnostics.json'
    with path.open('x', encoding='utf-8') as stream:
        os.chmod(path, 0o600)
        json.dump(report, stream, indent=2, allow_nan=False)
        stream.write('\n')
    print('Read-only broker diagnostics: ' + report['state'] + '; settlement remains unproved.')
    return 1 if report['state'] == 'partial' else 0


if __name__ == '__main__':
    try:
        if len(sys.argv) != 2:
            raise Refused('Expected one new absolute evidence directory')
        sys.exit(collect(Path(sys.argv[1])))
    except (Refused, Unavailable):
        sys.exit('Broker diagnostics refused: source/account/target could not be verified; no raw provider output retained.')
    except (OSError, ValueError, TypeError, KeyError, AttributeError):
        sys.exit('Broker diagnostics refused: incomplete observation; no raw provider output retained.')
