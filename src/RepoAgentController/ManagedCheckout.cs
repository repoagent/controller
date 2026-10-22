using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace RepoAgentController;

internal sealed record RepositoryCheckout(long Id, string Name, string Remote, string Branch);

/// <summary>Owns one persistent checkout, refusing ambiguous state without repairing user work.</summary>
internal sealed class ManagedCheckout : IDisposable
{
    const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    // These ownership records contain no credentials. An executable SMB view
    // reports owner execute on every file, without granting another user access.
    const UnixFileMode CheckoutMetadataMode = PrivateFileMode | UnixFileMode.UserExecute;
    readonly FileStream lease;
    public string Root { get; }
    readonly string record, git;
    readonly JsonObject metadata;
    readonly RepositoryCheckout repository;
    readonly GitHub github;
    readonly GitHubAppCredentials credentials;
    public string Owner => metadata.Text("owner");
    public long RepositoryId => repository.Id;
    public string DefaultRef => "refs/heads/" + repository.Branch;
    ManagedCheckout(string root, FileStream lease, string record, JsonObject metadata,
        RepositoryCheckout repository, string git, GitHub github, GitHubAppCredentials credentials)
    {
        Root = root; this.lease = lease; this.record = record; this.metadata = metadata;
        this.repository = repository; this.git = git; this.github = github; this.credentials = credentials;
    }

    public static async Task<ManagedCheckout> Open(string workspace, string state, GitHub github,
        GitHubAppCredentials credentials, string gh, CancellationToken ct)
    {
        workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        state = Path.GetFullPath(state);
        CheckPath(workspace); CheckPath(state);
        var root = Path.Combine(workspace, "repository");
        var record = Path.Combine(workspace, ".repoagent-checkout.json");
        var lockPath = Path.Combine(workspace, ".repoagent-checkout.lock");
        CheckPath(lockPath);
        if (File.Exists(lockPath)) _ = ReadFile(lockPath, CheckoutMetadataMode);
        var lease = new FileStream(lockPath, PrivateOptions(FileMode.OpenOrCreate));
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(workspace))
                if (entry != root && entry != record && entry != lockPath) throw Blocked("workspace contains unmanaged files");
            CheckPath(root); CheckPath(record);
            var repository = await github.CheckoutRepository(ct);
            if (repository.Id != credentials.RepositoryId || repository.Id <= 0) throw Blocked("repository identity differs from credential binding");
            JsonNode? saved = null;
            if (File.Exists(record))
            {
                saved = JsonNode.Parse(ReadFile(record, CheckoutMetadataMode)) ?? throw Blocked("invalid ownership record");
                if (saved["version"]?.GetValue<int>() != 1 || saved.Text("state") != state || saved.Text("root") != root ||
                    saved["repositoryId"]?.GetValue<long>() != repository.Id || saved.Text("repository") != repository.Name ||
                    saved.Text("remote") != repository.Remote || saved.Text("branch") != repository.Branch ||
                    !Guid.TryParseExact(saved.Text("owner"), "N", out _)) throw Blocked("ownership or repository binding differs");
                if (!Directory.Exists(root)) throw Blocked("owned checkout is missing");
            }
            else
            {
                if (Path.Exists(root)) throw Blocked("checkout has no ownership record");
                await credentials.GetToken(ct);
                await Run(AppGitHubCommand.Create(gh, credentials.TokenFile, ["repo", "clone", repository.Remote, root]), ct);
            }
            var git = AppGitHubCommand.FindExecutable("git");
            var commit = await Inspect(git, root, repository, ct);
            var ownerFile = Path.Combine(root, ".git", "repoagent-owner");
            if (saved is not null)
            {
                if (saved.Text("commit") != commit || ReadFile(ownerFile, CheckoutMetadataMode) != saved.Text("owner")) throw Blocked("checkout was replaced or advanced locally");
            }
            else
            {
                var remoteCommit = (await Local(git, root, ct, "rev-parse", "--verify", "refs/remotes/origin/" + repository.Branch + "^{commit}")).Trim();
                if (remoteCommit != commit) throw Blocked("cloned default branch changed during bootstrap");
                var owner = Guid.NewGuid().ToString("N");
                WriteNew(ownerFile, owner);
                saved = Json.Object(("version", 1), ("state", state), ("root", root), ("owner", owner),
                    ("repositoryId", repository.Id), ("repository", repository.Name), ("remote", repository.Remote),
                    ("branch", repository.Branch), ("commit", commit));
                WriteNew(record + ".tmp", saved.ToJsonString());
                File.Move(record + ".tmp", record);
            }
            Log.Write("checkout-ready", $"repository={repository.Name} root={root} commit={commit}");
            return new(root, lease, record, saved!.AsObject(), repository, git, github, credentials);
        }
        catch { lease.Dispose(); throw; }
    }

    static async Task<string> Inspect(string git, string root, RepositoryCheckout repository, CancellationToken ct)
    {
        CheckPath(root);
        var dotGit = Path.Combine(root, ".git");
        CheckPath(dotGit);
        if (!Directory.Exists(dotGit)) throw Blocked("checkout must have its own Git directory");
        foreach (var name in new[] { "config", "index", "HEAD", "repoagent-owner", "objects", "refs", "logs", "info" }) CheckPath(Path.Combine(dotGit, name));
        foreach (var name in new[] { "commondir", "gitdir", "objects/info/alternates", "info/grafts" })
            if (Path.Exists(Path.Combine(dotGit, name))) throw Blocked("linked Git storage");
        foreach (var name in new[] { "MERGE_HEAD",
            "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "BISECT_LOG", "rebase-apply", "rebase-merge", "sequencer", "index.lock", "HEAD.lock" })
            if (Path.Exists(Path.Combine(dotGit, name))) throw Deferred("in-progress Git operation");
        // Read only the local config without includes, before any command that could invoke a filter.
        var config = await Local(git, root, ct, "config", "--no-includes", "--local", "--null", "--list");
        foreach (var entry in config.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = entry.Split('\n', 2)[0].ToLowerInvariant();
            if (key is "core.repositoryformatversion" or "core.filemode" or "core.bare" or "core.logallrefupdates" or
                "core.ignorecase" or "core.precomposeunicode" or "remote.origin.url" or "remote.origin.fetch" or "remote.origin.gh-resolved") continue;
            if (key.StartsWith("branch.", StringComparison.Ordinal) && (key.EndsWith(".remote", StringComparison.Ordinal) || key.EndsWith(".merge", StringComparison.Ordinal))) continue;
            throw Blocked("unsupported local Git configuration");
        }
        if ((await Local(git, root, ct, "rev-parse", "--show-toplevel")).TrimEnd() != root ||
            (await Local(git, root, ct, "rev-parse", "--is-bare-repository")).Trim() != "false" ||
            (await Local(git, root, ct, "config", "--get-all", "remote.origin.url")).TrimEnd() != repository.Remote)
            throw Blocked("Git root or remote differs");
        if ((await Local(git, root, ct, "symbolic-ref", "--quiet", "HEAD")).Trim() != "refs/heads/" + repository.Branch)
            throw Deferred("checkout is not on its recorded default branch");
        var commit = (await Local(git, root, ct, "rev-parse", "--verify", "HEAD^{commit}")).Trim();
        if (await Local(git, root, ct, "status", "--porcelain=v1", "--untracked-files=all") != "") throw Deferred("checkout has local changes");
        var flags = await Local(git, root, ct, "ls-files", "-v", "-z");
        if (flags.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(line => line[0] == 'S' || char.IsLower(line[0])))
            throw Deferred("index hides working-tree changes");
        return commit;
    }

    // Safe refusals retain the recorded baseline while existing assistance may continue.
    internal sealed class DeferredRefresh(string reason) : IOException(reason);
    static DeferredRefresh Deferred(string reason) => new(reason);

    async Task VerifyBaseline(CancellationToken ct)
    {
        if (!JsonNode.DeepEquals(metadata, JsonNode.Parse(ReadFile(record, CheckoutMetadataMode))) ||
            ReadFile(Path.Combine(Root, ".git", "repoagent-owner"), CheckoutMetadataMode) != Owner)
            throw Blocked("ownership record changed");
        if (await Inspect(git, Root, repository, ct) != metadata.Text("commit"))
            throw Deferred("checkout advanced locally; recorded baseline retained");
    }

    public async Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct)
    {
        await VerifyBaseline(ct);
        RepositoryCheckout current;
        string target;
        try { current = await github.CheckoutRepository(ct); }
        catch (IOException error) when (error is not GitHub.CheckoutIdentityException)
        { throw Deferred("authoritative repository lookup unavailable; retry pending"); }
        if (current.Id != repository.Id || current.Name != repository.Name || current.Remote != repository.Remote)
            throw Blocked("authoritative repository identity changed");
        if (current.Branch != repository.Branch) throw Deferred("default branch changed; inspect the recorded binding");
        try { target = await github.CheckoutCommit(current.Branch, ct); }
        catch (IOException) { throw Deferred("authoritative default-branch lookup unavailable; retry pending"); }
        var previous = metadata.Text("commit");
        if (target == previous) return false;
        var fetched = Path.Combine(Root, ".git", "FETCH_HEAD");
        CheckPath(fetched);
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(credentials.TokenFile)!, "bin", "repoagent-git")) { UseShellExecute = false };
            foreach (var arg in new[] { "fetch", "--directory", Root }) start.ArgumentList.Add(arg);
            await Run(start, ct);
            CheckPath(fetched);
            if (new FileInfo(fetched).Length > 65536) throw Deferred("ambiguous fetched default branch");
            var lines = File.ReadAllLines(fetched);
            if (lines.Length != 1 || lines[0].Split('\t')[0] != target ||
                (await Local(git, Root, ct, "rev-parse", "--verify", "FETCH_HEAD^{commit}")).Trim() != target ||
                await github.CheckoutCommit(repository.Branch, ct) != target)
                throw Deferred("default branch changed during fetch; retry pending");
            if ((await Local(git, Root, ct, "merge-base", previous, target)).Trim() != previous)
                throw Deferred("default branch is not a fast-forward; manual recovery required");
        }
        catch (IOException error) { throw Deferred("fetch or ancestry check refused: " + error.Message); }
        try { current = await github.CheckoutRepository(ct); }
        catch (IOException error) when (error is not GitHub.CheckoutIdentityException)
        { throw Deferred("authoritative repository lookup unavailable after fetch; retry pending"); }
        if (current.Id != repository.Id || current.Name != repository.Name || current.Remote != repository.Remote)
            throw Blocked("authoritative repository identity changed during fetch");
        if (current.Branch != repository.Branch) throw Deferred("default branch changed during fetch");
        await VerifyBaseline(ct);
        beforeApply(previous, target);
        await Local(git, Root, ct, "-c", "submodule.recurse=false", "merge", "--ff-only", "--no-autostash", "--no-overwrite-ignore", target);
        if (await Inspect(git, Root, repository, ct) != target) throw Blocked("applied commit differs");
        metadata["commit"] = target;
        SaveFile(record, metadata.ToJsonString(), CheckoutMetadataMode);
        Log.Write("checkout-refreshed", $"root={Root} commit={target}");
        return true;
    }

    internal static void SavePrivate(string path, string value) => SaveFile(path, value, PrivateFileMode);

    static void SaveFile(string path, string value, UnixFileMode maximumMode)
    {
        CheckPath(path);
        if (File.Exists(path)) _ = ReadFile(path, maximumMode);
        WriteNew(path + ".tmp", value);
        File.Move(path + ".tmp", path, true);
    }

    internal static void CheckPath(string path)
    {
        if (path.Any(char.IsControl)) throw Blocked("control characters in managed paths");
        for (var item = Path.GetFullPath(path); item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item) || new FileInfo(item).LinkTarget is not null) &&
                (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw Blocked("symbolic links are not supported in managed paths");
    }

    internal static string ReadPrivate(string path) => ReadFile(path, PrivateFileMode);

    static string ReadFile(string path, UnixFileMode maximumMode)
    {
        CheckPath(path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 65536 || (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & ~maximumMode) != 0)) throw Blocked("private state is missing or unsafe");
        return File.ReadAllText(path);
    }

    static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    static void WriteNew(string path, string value)
    {
        CheckPath(path);
        using var file = new FileStream(path, PrivateOptions(FileMode.CreateNew));
        file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true);
    }
    static IOException Blocked(string reason) => new("Managed checkout blocked: " + reason + "; preserve this workspace and inspect it before retrying");

    static Task<string> Local(string git, string root, CancellationToken ct, params string[] arguments)
    {
        var start = new ProcessStartInfo(git) { UseShellExecute = false };
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.StartsWith("GIT_", StringComparison.Ordinal)) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        foreach (var arg in new[] { "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-C", root }.Concat(arguments)) start.ArgumentList.Add(arg);
        return Run(start, ct);
    }

    static async Task<string> Run(ProcessStartInfo start, CancellationToken ct)
    {
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var process = Process.Start(start) ?? throw Blocked("process could not start");
        async Task<string> Read(StreamReader reader)
        {
            var buffer = new char[4096]; var result = new StringBuilder();
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token);
                if (count == 0) return result.ToString();
                if (result.Length + count > 1024 * 1024) { deadline.Cancel(); throw Blocked("Git output exceeds bootstrap limit"); }
                result.Append(buffer, 0, count);
            }
        }
        var output = Read(process.StandardOutput); var error = Read(process.StandardError);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw Blocked("Git or clone command failed");
            return await output;
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            try { await Task.WhenAll(output, error); } catch { }
            throw;
        }
    }
    public void Dispose() => lease.Dispose();
}
