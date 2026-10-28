#!/usr/bin/env python3
"""One explicitly selected fixed authority command; only its deep allowlist is retained."""
import datetime as dt
import json
import os
from pathlib import Path
import re
import runpy
import sys
from types import SimpleNamespace

runtime = SimpleNamespace(**runpy.run_path(str(Path(__file__).with_name('broker-console-version.py'))))
guard = runtime.guard
SCHEMA = 'repoagent.authority-observation/v1'
COVERAGE = 'referenced-rows-only; gate reread is not a transaction snapshot or accounting reconciliation'
CLOCK = ('absent','at-or-before-observed-time','after-observed-time')
REFERENCE = ('absent','valid','invalid')
# Exact v1 DTO of RetainedAuthorityObservation and BrokerAuthorityObservationCommand.
# Never copy a provider object or accept new fields automatically.
ATTEMPT_FIELDS = dict(observedState=('reserved','sending','known','released','settled','redacted','unknown'),
    purpose=('runtime','verification','unknown'),ownerBinding=('match','not-checkable'),repositoryCharged='flag',
    scopeValid='flag',capacityReleased='flag',payloadRedacted='flag',settlementReason=('expired','revoked','unsent','unpublished','unknown','absent'),
    **{name:CLOCK for name in ('reservedAt','payloadPurgeAt','jwtIssuedAt','jwtExpiresAt','sendClaimedAt','expiresAt','releasedAt','settledAt','firstTerminalAt')})
FIELDS = dict(
    gate=dict(observedState=('vacant','active','unacknowledged','closed','unknown'),ownerReference=REFERENCE,
        verifierReference=REFERENCE,sequence='count',outstandingTokenCount='count',pendingCount='count'),
    owner=dict(observedState=('unacknowledged','active','revoked','redacted','unknown'),acknowledged='flag',payloadRedacted='flag',
        outstandingTokenCount='count',runtimeReference=REFERENCE,observationReferencePresent='boolean',
        **{name:CLOCK for name in ('expiresAt','lastActivityAt','firstTerminalAt','runtimeExpiresAt','runtimeObservationReservedAt',
                                  'runtimeObservationJwtIssuedAt','runtimeObservationJwtExpiresAt')}),
    receipt=dict(reason=('revoked-acknowledged','expired-unacknowledged','absent','unknown'),revokedAt=CLOCK,ownerReleasedAt=CLOCK),
    runtimeAttempt=ATTEMPT_FIELDS,verifierAttempt=ATTEMPT_FIELDS)


def invalid():
    raise guard.Unavailable('invalid-authority-projection')


def shape(value, keys):
    if not isinstance(value, dict) or set(value) != set(keys): invalid()


def choice(value, allowed):
    if not isinstance(value, str) or value not in allowed: invalid()
    return value


def integer(value, maximum):
    if type(value) is not int or not 0 <= value <= maximum: invalid()
    return value


def boolean(value):
    if type(value) is not bool: invalid()
    return value


def field(value, rule):
    if rule == 'count': return None if value is None else integer(value, 9223372036854775807)
    if rule == 'flag': return None if value is None else boolean(value)
    if rule == 'boolean': return boolean(value)
    return choice(value, rule)


def row(value, name):
    shape(value, ('read','complete','epochRelation','fields'))
    read = choice(value['read'], ('present','missing','not-referenced','invalid-reference','unavailable','unreadable','binding-mismatch'))
    complete = boolean(value['complete'])
    if read != 'present':
        if value['epochRelation'] is not None or complete != (read == 'not-referenced'): invalid()
        fields = None
        if value['fields'] is not None:
            if name not in ('runtimeAttempt','verifierAttempt') or read not in ('missing','unavailable','unreadable','binding-mismatch'): invalid()
            shape(value['fields'], ('referenceBinding',))
            choice(value['fields']['referenceBinding'], ('purpose-ambiguous',))
            fields = dict(referenceBinding='purpose-ambiguous')
        return dict(read=read,complete=complete,epochRelation=None,fields=fields)
    relation = choice(value['epochRelation'], ('match','mismatch','missing','invalid'))
    shape(value['fields'], FIELDS[name])
    fields = {key:field(value['fields'][key],rule) for key,rule in FIELDS[name].items()}
    if complete and (relation != 'match' or any(item is None or item in ('unknown','invalid','not-checkable') for item in fields.values())):
        invalid()
    return dict(read=read,complete=complete,epochRelation=relation,fields=fields)


def projection(value, expected):
    shape(value, ('schema','compiledVersion','compiledSource','observation'))
    if value['schema'] != SCHEMA or value['compiledVersion'] != expected['version'] or value['compiledSource'] != expected['source']: invalid()
    value = value['observation']
    shape(value, ('observedAt','readCount','state','gateObservation','gate','owner','receipt','runtimeAttempt','verifierAttempt',
                  'schema','scope','coverage','settlementProven','allOutstandingAttemptsExamined'))
    if value['schema'] != SCHEMA or value['scope'] != 'fixed-repository' or value['coverage'] != COVERAGE \
            or value['settlementProven'] is not False or value['allOutstandingAttemptsExamined'] is not False: invalid()
    observed = value['observedAt']
    if not isinstance(observed, str) or not re.fullmatch(r'[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|\+00:00)', observed): invalid()
    if dt.datetime.fromisoformat(observed.replace('Z','+00:00')).utcoffset() != dt.timedelta(0): invalid()
    count = integer(value['readCount'], 6)
    state = choice(value['state'], ('observed','partial','unavailable'))
    gate_observation = choice(value['gateObservation'], ('unchanged','changed','missing-on-reread','unavailable','not-checked'))
    rows = {name:row(value[name],name) for name in FIELDS}
    present = rows['gate']['read'] == 'present'
    if present != (state != 'unavailable') or present == (gate_observation == 'not-checked'): invalid()
    if present and count < 1 or gate_observation in ('unchanged','changed','missing-on-reread') and count < 2: invalid()
    fully_observed = all(item['complete'] for item in rows.values()) and gate_observation == 'unchanged'
    if (state == 'observed') != fully_observed: invalid()
    runtime_fields, verifier_fields = rows['runtimeAttempt']['fields'], rows['verifierAttempt']['fields']
    if runtime_fields == {'referenceBinding':'purpose-ambiguous'} or verifier_fields == {'referenceBinding':'purpose-ambiguous'}:
        if runtime_fields != verifier_fields or rows['runtimeAttempt']['read'] != rows['verifierAttempt']['read']: invalid()
    clean = dict(observedAt=observed,readCount=count,state=state,gateObservation=gate_observation,**rows,
        schema=SCHEMA,scope='fixed-repository',coverage=COVERAGE,settlementProven=False,allOutstandingAttemptsExamined=False)
    return dict(schema=SCHEMA,compiledVersion=expected['version'],compiledSource=expected['source'],observation=clean)


def one_projection(output, expected):
    def unique(pairs):
        result = {}
        for key,value in pairs:
            if key in result: invalid()
            result[key] = value
        return result
    candidates = []
    for line in output.splitlines():
        if line.lstrip().startswith(('{','[')):
            if len(line.encode('utf-8')) > 16384: invalid()
            candidates.append(json.loads(line, object_pairs_hook=unique, parse_constant=lambda _: invalid()))
    if len(candidates) != 1: invalid()
    return projection(candidates[0], expected)


def collect(output):
    tag = os.environ.get('AUTH_RELEASE_TAG','')
    if len(tag) > 40 or not re.fullmatch('broker-v'+runtime.release_policy['VERSION'],tag):
        raise guard.Refused('Unsupported authority command release')
    major,minor,_ = map(int,tag.removeprefix('broker-v').split('.'))
    if major != 1 or minor < 1: raise guard.Refused('Unsupported authority command release')
    image,revision,replica,tag,source = runtime.request()
    if not output.is_absolute() or output.exists(): raise guard.Refused('Use a new absolute evidence directory')
    output.mkdir(mode=0o700,parents=True)
    report = dict(schema='repoagent.broker-authority-probe/v1',source=source,observedAt=dt.datetime.now(dt.timezone.utc).isoformat(),
        target=dict(image=image,releaseTag=tag,revision=revision,container='auth-service'),state='unavailable',projectionValidated=False,
        remoteExitStatusEstablished=False,settlementProven=False,allOutstandingAttemptsExamined=False,
        limitations='Referenced-row observation only. Field completeness, remote exit status, all outstanding attempts and settlement are not proved by console transport.')
    try:
        expected = runtime.published_contract(image,tag)
        replica = runtime.selection(image,revision,replica,discover=True,authority=True)
        report['target']['replica'] = replica
        status,private_output = runtime.capture_console(revision,replica,authority=True)
        clean = one_projection(private_output,expected)
        runtime.selection(image,revision,replica,authority=True)
        state = clean['observation']['state']
        report.update(state='partial' if status and state == 'observed' else state,
            projectionValidated=True,cliExitSucceeded=status == 0,projection=clean)
    except (guard.Unavailable,ValueError,TypeError,KeyError,AttributeError,RecursionError) as error:
        report['reason'] = str(error) if isinstance(error,guard.Unavailable) else 'invalid-response'
    encoded = json.dumps(report,indent=2,allow_nan=False)+'\n'
    if len(encoded.encode('utf-8')) > 16384: raise guard.Unavailable('authority-artifact-limit')
    with (output/'authority-probe.json').open('x',encoding='utf-8') as stream:
        os.chmod(stream.name,0o600)
        stream.write(encoded)
    print('Authority console probe: '+report['state']+'; settlement and remote exit status remain unproved.')
    return 0 if report['projectionValidated'] and report['state'] == 'observed' else 1


if __name__ == '__main__':
    try:
        if len(sys.argv) != 2: raise guard.Refused('Expected one new absolute evidence directory')
        sys.exit(collect(Path(sys.argv[1])))
    except (guard.Refused,guard.Unavailable,OSError,ValueError,TypeError,KeyError,AttributeError,RecursionError):
        sys.exit('Authority probe refused: source/release/account/target could not be verified; no raw console output retained.')
