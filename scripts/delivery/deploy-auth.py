#!/usr/bin/env python3
"""Deploy only a labelled, immutable auth image to one preconfigured existing app."""
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time


def require(name, pattern):
    value = os.environ.get(name, '')
    if not re.fullmatch(pattern, value):
        raise ValueError(f'{name} is missing or invalid')
    return value


def configuration():
    uuid = r'[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}'
    values = {name: require(name, uuid) for name in
              ('AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID')}
    values['AUTH_RESOURCE_GROUP'] = require('AUTH_RESOURCE_GROUP', r'[A-Za-z0-9][A-Za-z0-9_.()-]{0,89}')
    values['AUTH_APP_NAME'] = require('AUTH_APP_NAME', r'[a-z][a-z0-9-]{0,30}[a-z0-9]')
    repository = require('AUTH_IMAGE_REPOSITORY', r'[a-z0-9][a-z0-9.-]+/[a-z0-9][a-z0-9/_.-]+')
    values['AUTH_IMAGE'] = require('AUTH_IMAGE', re.escape(repository) + r'@sha256:[0-9a-f]{64}')
    if os.environ.get('AUTH_DEPLOYMENT_READY') != 'true':
        raise ValueError('AUTH_DEPLOYMENT_READY must be true after service and infrastructure acceptance')
    return values


def read_json(args):
    # Never echo command output/errors: provider responses can contain configuration secrets.
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode:
        raise ValueError(f'{args[0]} command failed; inspect provider diagnostics separately')
    return json.loads(result.stdout)


def inspect_image():
    # Both manual rollback and automatic delivery use the same anonymous OCI contract.
    result = subprocess.run([sys.executable, str(Path(__file__).with_name('auth-pipeline.py')), 'attest'],
                            capture_output=True, text=True)
    if result.returncode:
        raise ValueError(result.stderr.strip() or 'Auth image attestation failed')


def deploy(config):
    target = ['--subscription', config['AZURE_SUBSCRIPTION_ID'], '--resource-group',
              config['AUTH_RESOURCE_GROUP'], '--name', config['AUTH_APP_NAME']]
    query = '{id:id,role:tags."repoagent-role",mode:properties.configuration.activeRevisionsMode,containers:properties.template.containers[].{name:name,image:image},latest:properties.latestRevisionName,ready:properties.latestReadyRevisionName}'
    def app():
        return read_json(['az', 'containerapp', 'show', *target, '--query', query, '-o', 'json'])
    before = app()
    expected_id = f"/subscriptions/{config['AZURE_SUBSCRIPTION_ID']}/resourceGroups/{config['AUTH_RESOURCE_GROUP']}/providers/Microsoft.App/containerApps/{config['AUTH_APP_NAME']}"
    if before.get('id', '').lower() != expected_id.lower() or before.get('role') != 'auth-service':
        raise ValueError('Existing app identity or repoagent-role tag does not match auth-service target')
    if before.get('mode') != 'Single' or len(before.get('containers', [])) != 1:
        raise ValueError('Deployment requires an existing single-container app in Single revision mode')
    result = subprocess.run(['az', 'containerapp', 'update', *target,
                             '--container-name', before['containers'][0]['name'],
                             '--image', config['AUTH_IMAGE'], '--output', 'none'], capture_output=True)
    if result.returncode:
        raise ValueError('Azure update failed; inspect the intended app in Azure')
    for _ in range(60):
        after = app()
        if after.get('containers') != [{'name': before['containers'][0]['name'], 'image': config['AUTH_IMAGE']}]:
            raise ValueError('Resulting app image differs from requested immutable image')
        if after.get('latest') and after.get('latest') == after.get('ready'):
            print('Intended immutable image is configured and latest revision is ready.')
            return
        time.sleep(10)
    raise ValueError('Revision did not become ready within 10 minutes; inspect app and roll back explicitly')


def main():
    config = configuration()
    if sys.argv[1:] == ['preflight']:
        inspect_image()
    elif sys.argv[1:] == ['deploy']:
        inspect_image()
        deploy(config)
    else:
        raise ValueError('Expected preflight or deploy')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError) as error:
        sys.exit(str(error))
