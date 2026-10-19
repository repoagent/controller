"""Non-root startup updates; pinned image tools remain available for reproducible runs."""
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request

TOOLS = Path('/data/tools')
BIN = TOOLS / 'bin'


def fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'repoagent-startup-update', 'Accept': 'application/vnd.github+json'})
    return urllib.request.urlopen(request, timeout=60)


def update_gh():
    arch = {'x86_64': 'amd64', 'aarch64': 'arm64'}.get(platform.machine())
    if arch is None:
        raise ValueError('Startup updates support linux/amd64 and linux/arm64.')
    with fetch('https://api.github.com/repos/cli/cli/releases/latest') as response:
        release = json.load(response)
    version = release['tag_name'].removeprefix('v')
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Latest gh release has an unsupported version tag.')
    name = f'gh_{version}_linux_{arch}.tar.gz'
    asset = next((a for a in release['assets'] if a['name'] == name), None)
    if asset is None:
        raise ValueError(f'Latest gh release has no supported asset {name}.')
    digest = asset.get('digest', '')
    if not digest.startswith('sha256:') or len(digest) != 71:
        raise ValueError('Latest gh release has no usable published SHA-256 digest.')
    destination = TOOLS / 'gh' / version / 'gh'
    if not destination.exists():
        with tempfile.TemporaryDirectory(dir=TOOLS) as directory:
            archive = Path(directory) / name
            with fetch(asset['browser_download_url']) as response, archive.open('wb') as output:
                shutil.copyfileobj(response, output)
            with archive.open('rb') as source:
                actual = hashlib.file_digest(source, 'sha256').hexdigest()
            if actual != digest.removeprefix('sha256:'):
                raise ValueError('Downloaded gh archive checksum differs from the release metadata.')
            with tarfile.open(archive) as package:
                member = package.extractfile(f'gh_{version}_linux_{arch}/bin/gh')
                if member is None:
                    raise ValueError('Latest gh archive contains no gh executable.')
                binary = Path(directory) / 'gh'
                with binary.open('wb') as output:
                    shutil.copyfileobj(member, output)
            binary.chmod(0o700)
            subprocess.run([str(binary), '--version'], check=True)
            destination.parent.mkdir(parents=True, exist_ok=True)
            binary.replace(destination)
    temporary = BIN / 'gh.next'
    temporary.unlink(missing_ok=True)
    temporary.symlink_to(destination)
    temporary.replace(BIN / 'gh')
    actual_version = subprocess.check_output([str(BIN / 'gh'), '--version'], text=True).split()[2]
    if actual_version != version:
        raise ValueError('Installed gh version differs from the latest stable release.')
    print(f'Startup gh update: latest stable {version}.', flush=True)


def update_codex():
    environment = os.environ | {'CODEX_HOME': str(TOOLS / 'codex-home'),
                                'CODEX_INSTALL_DIR': str(BIN), 'CODEX_NON_INTERACTIVE': '1',
                                'PATH': str(BIN) + ':/usr/sbin:/usr/bin:/sbin:/bin'}
    executable = BIN / 'codex'
    if not executable.exists():
        with tempfile.TemporaryDirectory(dir=TOOLS) as directory:
            installer = Path(directory) / 'codex-install.sh'
            subprocess.run(['curl', '--fail', '--location', '--retry', '3', '--output', str(installer),
                            'https://chatgpt.com/codex/install.sh'], check=True, timeout=120)
            subprocess.run(['sh', str(installer)], env=environment, check=True, timeout=240)
    # The official managed installation updates Codex and its required companion binaries together.
    subprocess.run([str(executable), 'update'], env=environment, check=True, timeout=240)


def update():
    os.umask(0o077)
    BIN.mkdir(parents=True, exist_ok=True)
    update_gh()
    update_codex()
    resolved = {name: subprocess.check_output([str(BIN / name), '--version'], text=True).strip() for name in ('gh', 'codex')}
    (TOOLS / 'resolved.json').write_text(json.dumps(resolved, indent=2) + '\n')
    print('Startup tool versions: ' + json.dumps(resolved), flush=True)
