"""Container-owned configuration, skill installation and runtime preflight."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request
import urllib.parse
import update_tools
import quick_tunnel
import yaml

CONFIG = Path('/data/config/settings.json')
CODEX = Path('/data/credentials/codex')
SKILLS = Path('/data/config/skills')
STATE = '/data/state/bridge.json'
CONTROLLER = ['/opt/repoagent/controller/RepoAgentController']


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
            '--workspace', '/workspace', '--state', STATE]
    if config.get('brokerUrl'):
        args += ['--broker-url', config['brokerUrl'], '--enrollment-file', '/data/enrollment/enrollment.json']
    else:
        args += ['--private-key', '/run/secrets/github-app.pem']
    if config.get('installationId'):
        args += ['--installation-id', str(config['installationId'])]
    if config.get('mentionUser'):
        args += ['--mention-user', config['mentionUser']]
    if config.get('mode', 'polling') in ('fixed-url', 'quick-tunnel'):
        args += ['--listen-port', str(config.get('listenPort', 8080)),
                 '--reconcile-interval', str(config.get('reconcileInterval', 300))]
        if config['mode'] == 'fixed-url':
            args += ['--webhook-url', config['publicUrl']]
    for url in config.get('enroll', []):
        args += ['--enroll', url]
    return args


def select_tools(config):
    paths = [p for p in os.environ['PATH'].split(os.pathsep) if p != '/data/tools/bin']
    if config.get('toolUpdates', 'latest') == 'latest':
        paths.insert(0, '/data/tools/bin')
    os.environ['PATH'] = os.pathsep.join(paths)


def configure(args):
    config = settings() if CONFIG.exists() else {}
    if not re.fullmatch(r'[\w.-]+/[\w.-]+', args.repository):
        raise ValueError('Repository must be OWNER/REPO.')
    if not re.fullmatch(r'(?:[0-9]+|Iv[A-Za-z0-9.]+)', args.app_id):
        raise ValueError('Supply the App ID or client ID, not a token.')
    broker_url = getattr(args, 'broker_url', None)
    enrollment_file = getattr(args, 'enrollment_file', None)
    broker = bool(broker_url or enrollment_file)
    if broker:
        if not broker_url or not enrollment_file or args.key_path:
            raise ValueError('Supply both --broker-url and --enrollment-file, without --key-path.')
        url = urllib.parse.urlsplit(broker_url)
        if url.scheme != 'https' or not url.hostname or url.username or url.password or url.path not in ('', '/') or url.query or url.fragment:
            raise ValueError('Broker URL must be a trusted HTTPS origin.')
    elif not args.key_path or not (Path(args.key_path).is_absolute() or PureWindowsPath(args.key_path).is_absolute()) or any(c in args.key_path for c in ',\n\r'):
        raise ValueError('Private-key path must be an absolute host path without commas/newlines.')
    if config and bool(config.get('brokerUrl')) != broker:
        raise ValueError('Changing credential mode requires a fresh instance.')
    if config.get('brokerUrl') and config['brokerUrl'] != broker_url:
        raise ValueError('Changing broker requires a fresh instance.')
    if args.mention_user and not re.fullmatch(r'[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*', args.mention_user):
        raise ValueError('Mention user must be a GitHub login without @.')
    if config and any(config.get(k) != v for k, v in [('repository', args.repository), ('appId', args.app_id)]):
        raise ValueError('Changing repository/App requires a new REPOAGENT_INSTANCE to preserve existing conversations.')
    if args.installation_id is not None and args.installation_id <= 0:
        raise ValueError('Expected installation ID must be positive.')
    if not 1 <= args.listen_port <= 65535 or args.reconcile_interval < 1:
        raise ValueError('Listener port must be 1..65535 and reconciliation seconds must be positive.')
    if args.mode == 'fixed-url':
        url = urllib.parse.urlsplit(args.public_url)
        if url.scheme != 'https' or not url.hostname or url.username or url.password or url.path not in ('', '/') or url.query or url.fragment:
            raise ValueError('Fixed URL needs a public HTTPS origin without credentials, path, query or fragment.')
    config.update(repository=args.repository, appId=args.app_id, keyPath=args.key_path,
                  brokerUrl=broker_url,
                  installationId=args.installation_id, mentionUser=args.mention_user, model=args.model, webSearch=args.web_search,
                  toolUpdates=args.tool_updates, mode=args.mode, publicUrl=args.public_url,
                  listenPort=args.listen_port, reconcileInterval=args.reconcile_interval)
    save(config)
    CODEX.mkdir(parents=True, exist_ok=True)
    # The container is the operator-selected execution boundary. The bridge has no approval UI.
    (CODEX / 'config.toml').write_text('model = ' + json.dumps(args.model) + '\nweb_search = ' + json.dumps(args.web_search) + '\napproval_policy = "never"\nsandbox_mode = "danger-full-access"\ncli_auth_credentials_store = "file"\n')
    print(f"Configured {args.repository}; App {args.app_id}; model {args.model}.")


def import_enrollment():
    """Transport only: the native consumer owns all credential policy."""
    directory = Path('/data/enrollment')
    info = directory.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != 1000 or stat.S_IMODE(info.st_mode) != 0o700:
        raise ValueError('Enrollment volume must be a private UID1000 directory.')
    target = directory / 'enrollment.json'
    def check_target():
        try:
            existing = target.lstat()
        except FileNotFoundError:
            return
        if not stat.S_ISREG(existing.st_mode) or existing.st_uid != 1000 or stat.S_IMODE(existing.st_mode) != 0o600 or existing.st_nlink != 1:
            raise ValueError('Unsafe existing enrollment target; refusing import.')
    check_target()
    data = sys.stdin.buffer.read(65537)
    if not data or len(data) > 65536:
        raise ValueError('Enrollment input must contain 1..65536 bytes.')
    descriptor, temporary = tempfile.mkstemp(prefix='.enrollment-', dir=directory)
    try:
        with os.fdopen(descriptor, 'wb') as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        check_target()
        os.replace(temporary, target)
        descriptor = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


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
            if not isinstance(item, dict) or not all(isinstance(item.get(key), str) for key in ('repository', 'revision', 'path')):
                raise ValueError('Each selected skill needs string repository, revision and path fields.')
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
            header = re.match(r'\A---\n(.*?)\n---(?:\n|\Z)', text, re.DOTALL)
            try:
                metadata = yaml.safe_load(header.group(1)) if header else None
            except yaml.YAMLError:
                metadata = None
            if not isinstance(metadata, dict) or not isinstance(metadata.get('name'), str) or not isinstance(metadata.get('description'), str) or not metadata['description'].strip():
                raise ValueError(f'Invalid skill metadata at {repo}:{location}.')
            name = metadata['name']
            if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,63}', name):
                raise ValueError(f'Invalid skill name at {repo}:{location}.')
            if name == 'github-discussion' or (staging / name).exists():
                raise ValueError(f'Duplicate or reserved skill name: {name}.')
            if any(p.is_symlink() for p in source.rglob('*')):
                raise ValueError(f'Skill {name} contains symlinks; use regular files.')
            shutil.copytree(source, staging / name)
            resolved.append(dict(name=name, repository=repo, revision=revision, path=location))
        SKILLS.mkdir(exist_ok=True)
        # The operator must keep setup/start commands sequential; the host CLI rejects a running agent.
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
    configure_parser.add_argument('--key-path')
    configure_parser.add_argument('--broker-url')
    configure_parser.add_argument('--enrollment-file')
    configure_parser.add_argument('--installation-id', type=int)
    configure_parser.add_argument('--mention-user', default='repoagent')
    configure_parser.add_argument('--model', default='gpt-6.1-sol')
    configure_parser.add_argument('--web-search', choices=['live', 'cached', 'disabled'], default='live')
    configure_parser.add_argument('--tool-updates', choices=['latest', 'pinned'], default='latest')
    configure_parser.add_argument('--mode', choices=['polling', 'fixed-url', 'quick-tunnel'], default='polling')
    configure_parser.add_argument('--public-url', default='')
    configure_parser.add_argument('--listen-port', type=int, default=8080)
    configure_parser.add_argument('--reconcile-interval', type=int, default=300)
    get = sub.add_parser('get'); get.add_argument('key')
    login = sub.add_parser('login')
    login.add_argument('--non-interactive', action='store_true')
    for name in ['import-enrollment', 'versions', 'check', 'start', 'participant-login', 'participant-identity', 'state', 'skills']:
        sub.add_parser(name)
    skills = sub.add_parser('install-skills'); skills.add_argument('selection'); skills.add_argument('--token-file')
    enroll = sub.add_parser('enroll'); enroll.add_argument('urls', nargs='+')
    args = parser.parse_args()
    select_tools(settings() if CONFIG.exists() else {})
    if args.command == 'import-enrollment': import_enrollment()
    elif args.command == 'configure': configure(args)
    elif args.command == 'get': print(settings().get(args.key) or '')
    elif args.command == 'versions':
        print('RepoAgent container; uid=' + str(os.getuid()), flush=True)
        print('RepoAgentController: Native AOT, built with .NET SDK 11.0.100-rc.2', flush=True)
        run([sys.executable, '--version'])
        for name in ['gh', 'codex', 'rg', 'cloudflared']:
            if shutil.which(name): run([name, '--version'])
    elif args.command == 'install-skills': install_skills(args.selection, args.token_file)
    elif args.command == 'skills': print((CONFIG.parent / 'skills.json').read_text() if (CONFIG.parent / 'skills.json').exists() else '[]')
    elif args.command == 'login':
        if subprocess.run(['codex', 'login', 'status']).returncode:
            if args.non_interactive:
                raise ValueError('No valid container Codex sign-in. Supply --codex-auth to the installer or rerun interactively to sign in.')
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
        if args.command == 'start' and config.get('toolUpdates', 'latest') == 'latest':
            update_tools.update()
        run(['codex', 'login', 'status'])
        if args.command == 'check':
            # Check the webhook grant without creating a tunnel or registering a hook.
            endpoint = ['--webhook-url', 'https://quick-tunnel.invalid'] if config.get('mode') == 'quick-tunnel' else []
            run(CONTROLLER + bridge_args(config) + endpoint + ['--check-auth'])
        elif config.get('mode') == 'quick-tunnel':
            sys.exit(quick_tunnel.run(CONTROLLER + bridge_args(config), config.get('listenPort', 8080)))
        else:
            os.execv(CONTROLLER[0], CONTROLLER + bridge_args(config))


if __name__ == '__main__':
    try: main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        print('RepoAgent setup/runtime failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
