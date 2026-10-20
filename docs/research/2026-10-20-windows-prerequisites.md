# Windows prerequisites for RepoAgent

Checked 2026-10-20 against Microsoft, Docker and GitHub documentation and this checkout. This is documentation research; Windows execution, Docker Desktop integration and a new GitHub App installation were not tested. No credentials were read or external configuration changed.

These WSL findings apply to the Bash launcher. The subsequently added native `install.ps1` and `repoagent.ps1` use Docker Desktop directly and do not require WSL; see the [Windows installation guide](../container-setup.md#install-on-windows).

## Windows and Docker Desktop

Use an Ubuntu shell in WSL 2 for RepoAgent's host commands. The repository's [container guide](../container-setup.md) requires Bash, and the [host wrapper](../../repoagent) uses Bash arrays and a Bash shebang. Recommending WSL rather than entering those commands directly in PowerShell follows from that implementation. The application tools themselves run inside containers.

In administrator PowerShell, inspect WSL and update it:

```powershell
wsl --update
wsl --list --verbose
```

If Ubuntu is missing, install it, restart when requested, then launch Ubuntu and create its Linux username/password:

```powershell
wsl --install -d Ubuntu
```

If an existing Ubuntu distribution shows `VERSION 1`, convert that named distribution:

```powershell
wsl --set-version Ubuntu 2
```

Use the exact distribution name shown by `wsl --list --verbose` if it differs. Open its shell with `wsl -d Ubuntu`. Microsoft documents these install, list, update and distribution commands. [Install WSL](https://learn.microsoft.com/en-us/windows/wsl/install), [WSL commands](https://learn.microsoft.com/en-us/windows/wsl/basic-commands).

Docker requires WSL version 2.1.5 or newer and recommends the latest version. In Docker Desktop:

1. Open **Settings → General** and enable **Use WSL 2 based engine**, if shown. Docker says the setting may be enabled by default and hidden on supported systems.
2. Open **Settings → Resources → WSL Integration** and enable integration for the Ubuntu distribution being used.
3. Select **Apply**.

If WSL Integration is absent, use the Docker taskbar menu to **Switch to Linux containers**. Use Docker Desktop's integration; installing an additional Docker Engine/CLI inside Ubuntu can conflict with it. [Docker's WSL backend guide](https://docs.docker.com/desktop/features/wsl/).

In Ubuntu, install Git if necessary, then keep the checkout in the Linux filesystem:

```bash
sudo apt-get update
sudo apt-get install -y git
mkdir -p ~/src
cd ~/src
git clone https://github.com/egil/repoagent.git
cd repoagent
docker version
docker info --format '{{.OSType}}'
```

The final command should report `linux`. Microsoft recommends Linux files under `/home/...` for Linux shell workflows because working across the Windows filesystem costs performance. [WSL filesystems](https://learn.microsoft.com/en-us/windows/wsl/filesystems). A Linux-side clone also supplies the executable shell scripts and Linux paths expected by the wrapper.

Keep the App key outside the checkout and supply its absolute Linux path to setup. The container runs as UID 1000; the selected PEM must be readable by UID 1000. Setup does not alter host permissions. Confirm the Ubuntu user with `id -u` before relying on a user-owned `chmod 600` key. These requirements come from the repository's [storage documentation](../container-setup.md#storage-and-lifecycle), rather than a Windows-specific provider guarantee.

## Reuse an existing Codex sign-in

Codex credentials can be in `auth.json` or an OS credential store. File storage uses `CODEX_HOME/auth.json`, with `CODEX_HOME` defaulting to `~/.codex`. Native Windows Codex uses `%USERPROFILE%\.codex`; WSL Codex uses the Linux home unless configured otherwise. Consequently, from Ubuntu the usual Windows cache is `/mnt/c/Users/WINDOWS_USER/.codex/auth.json`, while a WSL sign-in is usually `$HOME/.codex/auth.json`. Replace those defaults when the signed-in environment uses a custom `CODEX_HOME`. [OpenAI authentication documentation](https://learn.chatgpt.com/docs/auth), [Windows and WSL Codex homes](https://learn.chatgpt.com/docs/app/windows).

RepoAgent provides `./repoagent import-codex-auth /absolute/path/to/auth.json` to copy an explicitly selected file into its container credential volume, followed by `./repoagent check`. It does not automatically share the host login. If credentials are only in the OS credential store and there is no file to import, use `./repoagent login` for the container sign-in instead. Device authentication must be allowed by the ChatGPT account/workspace. The import behavior and commands come from the [container guide](../container-setup.md) and [host wrapper](../../repoagent); OpenAI documents cache-copy authentication and its credential-store limitation in the [authentication guide](https://learn.chatgpt.com/docs/auth).

## GitHub organization and private repository

For one independently controlled organization, register the App under that organization and select **Only on this account**. A private App can be installed only on the account that owns the registration. A personal-account-owned private App cannot be installed in another organization, even when the same human controls both. An App registered elsewhere needs **Any account** visibility to install there. App visibility is separate from whether the target repository is private. [App visibility](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/making-a-github-app-public-or-private), [installing your own App](https://docs.github.com/en/apps/using-github-apps/installing-your-own-github-app).

In the App registration's **Install App** flow, choose the organization, **Only select repositories**, and the target repository. Repository selection is part of the installation, including for private repositories. [Installation instructions](https://docs.github.com/en/apps/using-github-apps/installing-your-own-github-app).

Organization policy can limit installation and repository additions to organization owners. When the restriction is enabled, repository administrators must request installation; disabling access requests as well blocks that request route. An organization owner should complete installation when the organization enforces this restriction. [GitHub organization policy](https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations).
