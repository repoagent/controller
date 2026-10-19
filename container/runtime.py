"""Container-owned configuration, skill installation and runtime preflight."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request

CONFIG = Path('/data/config/settings.json')
CODEX = Path('/data/config/codex')
SKILLS = Path('/data/config/skills')
STATE = '/data/state/bridge.json'
BRIDGE = ['/opt/bridge/bridge']


def run(args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


def settings():
    if not CONFIG.is_file():
        raise ValueError('Run ./repoagent setup first (or configure for noninteractive setup).')
    return json.loads(CONFIG.read_text())


def save(value):
    CONFIG.parent.mkdir(parents=True, exist_ok=True)
    temporary = CONFIG.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2) + '\n')
    temporary.chmod(0o600)
    temporary.replace(CONFIG)


def bridge_args(config):
    args = ['--repo', config['repository'], '--app-id', config['appId'],
            '--private-key', '/run/secrets/github-app.pem', '--workspace', '/workspace', '--state', STATE]
    if config.get('installationId'):
        args += ['--installation-id', str(config['installationId'])]
    if config.get('mentionUser'):
        args += ['--mention-user', config['mentionUser']]
    for url in config.get('enroll', []):
        args += ['--enroll', url]
    return args


def configure(args):
    config = settings() if CONFIG.exists() else {}
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', args.repository):
        raise ValueError('Repository must be OWNER/REPO.')
    if not re.fullmatch(r'(?:[0-9]+|Iv[A-Za-z0-9.]+)', args.app_id):
        raise ValueError('Supply the App ID or client ID, not a token.')
    if not Path(args.key_path).is_absolute() or any(c in args.key_path for c in ',\n\r'):
        raise ValueError('Private-key path must be an absolute host path without commas/newlines.')
    if args.mention_user and not re.fullmatch(r'[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*', args.mention_user):
        raise ValueError('Mention user must be a GitHub login without @.')
    if config and any(config.get(k) != v for k, v in [('repository', args.repository), ('appId', args.app_id)]):
        raise ValueError('Changing repository/App requires a new REPOAGENT_INSTANCE to preserve existing conversations.')
    if args.installation_id is not None and args.installation_id <= 0:
        raise ValueError('Expected installation ID must be positive.')
    config.update(repository=args.repository, appId=args.app_id, keyPath=args.key_path,
                  installationId=args.installation_id, mentionUser=args.mention_user, model=args.model, webSearch=args.web_search)
    save(config)
    CODEX.mkdir(parents=True, exist_ok=True)
    # The container is the operator-selected execution boundary. The bridge has no approval UI.
    (CODEX / 'config.toml').write_text('model = ' + json.dumps(args.model) + '\nweb_search = ' + json.dumps(args.web_search) + '\napproval_policy = "never"\nsandbox_mode = "danger-full-access"\ncli_auth_credentials_store = "file"\n')
    print(f"Configured {args.repository}; App {args.app_id}; model {args.model}.")


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        redirected = super().redirect_request(req, fp, code, msg, headers, newurl)
        if redirected is not None:
            redirected.remove_header('Authorization')
        return redirected


def install_skills(selection_path, token_path=None):
    selection = json.loads(Path(selection_path).read_text())
    if not isinstance(selection, list):
        raise ValueError('Skills configuration must be a JSON array.')
    resolved = []
    with tempfile.TemporaryDirectory(dir=SKILLS.parent) as directory:
        staging = Path(directory) / 'skills'
        staging.mkdir()
        for index, item in enumerate(selection):
            repo, revision, location = item['repository'], item['revision'], item['path']
            if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repo) or not re.fullmatch(r'[0-9a-f]{40}', revision):
                raise ValueError('Each skill needs OWNER/REPO and a full immutable 40-character commit SHA.')
            parts = PurePosixPath(location)
            if parts.is_absolute() or '..' in parts.parts or '\\' in location:
                raise ValueError('Skill path must stay within its source repository.')
            request = urllib.request.Request(f'https://api.github.com/repos/{repo}/tarball/{revision}', headers={'User-Agent': 'repoagent-skill-setup'})
            if item.get('private'):
                if not token_path:
                    raise ValueError('Private skills require an explicitly supplied --skill-token-file with source access.')
                request.add_header('Authorization', 'Bearer ' + Path(token_path).read_text().strip())
            archive = Path(directory) / f'{index}.tar.gz'
            try:
                with urllib.request.build_opener(SafeRedirect()).open(request, timeout=60) as response, archive.open('wb') as target:
                    shutil.copyfileobj(response, target)
            except urllib.error.HTTPError as error:
                raise ValueError(f'Skill source {repo}@{revision} unavailable (HTTP {error.code}); check revision, authorization and source repository permissions.') from None
            extracted = Path(directory) / str(index)
            with tarfile.open(archive) as tar:
                tar.extractall(extracted, filter='data')
            roots = list(extracted.iterdir())
            source = (roots[0] / location).resolve()
            if not source.is_relative_to(extracted.resolve()) or not (source / 'SKILL.md').is_file():
                raise ValueError(f'Missing skill at {repo}:{location}.')
            text = (source / 'SKILL.md').read_text()
            match = re.search(r'^name:\s*[\'"]?([a-z0-9][a-z0-9-]*)[\'"]?\s*$', text, re.MULTILINE)
            if not text.startswith('---\n') or not match or not re.search(r'^description:\s*\S', text, re.MULTILINE):
                raise ValueError(f'Invalid skill metadata at {repo}:{location}.')
            name = match.group(1)
            if name == 'github-discussion' or (staging / name).exists():
                raise ValueError(f'Duplicate or reserved skill name: {name}.')
            if any(p.is_symlink() for p in source.rglob('*')):
                raise ValueError(f'Skill {name} contains symlinks; use regular files.')
            shutil.copytree(source, staging / name)
            resolved.append(dict(name=name, repository=repo, revision=revision, path=location))
        SKILLS.mkdir(exist_ok=True)
        # Setup/start are serialized by the host CLI; never replace skills during native work.
        for old in SKILLS.iterdir():
            if old.is_dir(): shutil.rmtree(old)
            else: old.unlink()
        for skill in staging.iterdir(): shutil.move(str(skill), SKILLS / skill.name)
    (CONFIG.parent / 'skills.json').write_text(json.dumps(resolved, indent=2) + '\n')
    print(json.dumps({'required': 'github-discussion', 'selected': resolved}, indent=2))


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest='command', required=True)
    configure_parser = sub.add_parser('configure')
    configure_parser.add_argument('--repository', required=True)
    configure_parser.add_argument('--app-id', required=True)
    configure_parser.add_argument('--key-path', required=True)
    configure_parser.add_argument('--installation-id', type=int)
    configure_parser.add_argument('--mention-user', default='repoagent')
    configure_parser.add_argument('--model', default='gpt-6.1-sol')
    configure_parser.add_argument('--web-search', choices=['live', 'cached', 'disabled'], default='live')
    get = sub.add_parser('get'); get.add_argument('key')
    for name in ['versions', 'check', 'start', 'login', 'participant-login', 'participant-identity', 'state', 'skills']:
        sub.add_parser(name)
    skills = sub.add_parser('install-skills'); skills.add_argument('selection'); skills.add_argument('--token-file')
    enroll = sub.add_parser('enroll'); enroll.add_argument('urls', nargs='+')
    args = parser.parse_args()
    if args.command == 'configure': configure(args)
    elif args.command == 'get': print(settings().get(args.key) or '')
    elif args.command == 'versions':
        print('RepoAgent container; uid=' + str(os.getuid()), flush=True)
        print('bridge: Native AOT, built with .NET SDK 11.0.100-rc.2', flush=True)
        for command in [['gh', '--version'], ['codex', '--version'], ['python3', '--version'], ['rg', '--version']]: run(command)
    elif args.command == 'install-skills': install_skills(args.selection, args.token_file)
    elif args.command == 'skills': print((CONFIG.parent / 'skills.json').read_text() if (CONFIG.parent / 'skills.json').exists() else '[]')
    elif args.command == 'login':
        if subprocess.run(['codex', 'login', 'status']).returncode:
            result = subprocess.run(['codex', 'login', '--device-auth'])
            if result.returncode:
                raise ValueError('Enable device-code login in ChatGPT security/workspace settings, then rerun login. Alternatively use ./repoagent import-codex-auth /explicit/path/auth.json. No host sign-in is changed.')
    elif args.command == 'participant-login':
        os.environ['GH_CONFIG_DIR'] = '/data/participant'
        run(['gh', 'auth', 'login', '--hostname', 'github.com', '--git-protocol', 'https', '--web', '--skip-ssh-key'])
    elif args.command == 'participant-identity':
        os.environ['GH_CONFIG_DIR'] = '/data/participant'; run(['gh', 'api', 'user', '--jq', '.login'])
    elif args.command == 'enroll':
        config = settings()
        for url in args.urls:
            if not re.fullmatch('https://github.com/' + re.escape(config['repository']) + r'/discussions/\d+', url):
                raise ValueError('Enrollment URL must belong to the configured repository.')
        config['enroll'] = list(dict.fromkeys(config.get('enroll', []) + args.urls)); save(config)
    elif args.command == 'state': print(Path(STATE).read_text())
    elif args.command in ('check', 'start'):
        config = settings()
        run(['codex', 'login', 'status'])
        if args.command == 'check': run(BRIDGE + bridge_args(config) + ['--check-auth'])
        else: os.execv(BRIDGE[0], BRIDGE + bridge_args(config))


if __name__ == '__main__':
    try: main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        print('RepoAgent setup/runtime failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
