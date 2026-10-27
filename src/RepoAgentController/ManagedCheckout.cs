using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Owns one persistent, disposable checkout of the bound origin mainline, refusing foreign or unsafe state.</summary>
internal sealed class ManagedCheckout : IRepositoryCheckout, IDisposable
{
    const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    // These ownership records contain no credentials. An executable SMB view
    // reports owner execute on every file, without granting another user access.
    const UnixFileMode CheckoutMetadataMode = PrivateFileMode | UnixFileMode.UserExecute;
    readonly FileStream lease;
    public string Root { get; }
    readonly CheckoutRecord metadata;
    readonly RepositoryCheckout repository;
    readonly CheckoutUpdate update;
    public string Owner => metadata.Owner!;
    public long RepositoryId => repository.Id;
    public string DefaultRef => "refs/heads/" + repository.Branch;
    ManagedCheckout(string root, FileStream lease, Workspace workspace, CheckoutRecord metadata,
        RepositoryCheckout repository, CheckoutFacts facts)
    {
        Root = root; this.lease = lease; this.metadata = metadata; this.repository = repository;
        update = new(workspace, facts, root, new(repository, metadata), new ControllerLog());
    }

    public static async Task<ManagedCheckout> Open(string workspace, string state, GitHub github,
        GitHubAppCredentials credentials, string gh, CancellationToken ct)
    {
        workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        state = Path.GetFullPath(state);
        CheckPath(workspace); CheckPath(state);
        var root = Path.Combine(workspace, CheckoutInspection.RootName);
        var record = Path.Combine(workspace, CheckoutInspection.RecordName);
        var lockPath = Path.Combine(workspace, CheckoutInspection.LockName);
        CheckPath(lockPath);
        if (File.Exists(lockPath)) _ = ReadFile(lockPath, CheckoutMetadataMode);
        var lease = ExclusiveFileLease.Open(lockPath);
        try
        {
            CheckoutInspection.Workspace(Directory.EnumerateFileSystemEntries(workspace).Select(entry => Path.GetFileName(entry)));
            CheckPath(root); CheckPath(record);
            var facts = new CheckoutFacts(root);
            var files = new Workspace(root, record, github, credentials, gh);
            var opened = await CheckoutInspection.Open(files, facts, state, root, credentials.RepositoryId, ct);
            Log.Write("checkout-ready", $"repository={opened.Repository.Name} root={root} commit={opened.Record.Commit}");
            return new(root, lease, files, opened.Record, opened.Repository, facts);
        }
        catch { lease.Dispose(); throw; }
    }

    /// <summary>
    /// The workspace's files, its clone and the authoritative repository lookup. It keeps the record's text as last read or
    /// written, which a refresh compares with the file as JSON before it changes the checkout.
    /// </summary>
    sealed class Workspace(string root, string record, GitHub github, GitHubAppCredentials credentials, string gh) : ICheckoutUpdateWorkspace
    {
        readonly string ownerFile = Path.Combine(root, ".git", "repoagent-owner");
        string recorded = "";

        public Task<RepositoryCheckout> AuthoritativeRepository(CancellationToken ct) => github.CheckoutRepository(ct);
        public Task<string> AuthoritativeCommit(string branch, CancellationToken ct) => github.CheckoutCommit(branch, ct);

        public void PrepareFetch() => CheckPath(Path.Combine(root, ".git", "FETCH_HEAD"));

        public async Task<FetchedBranch> Fetch(CancellationToken ct)
        {
            var start = new ProcessStartInfo(Path.Combine(credentials.CredentialDirectory, "bin", "repoagent-git")) { UseShellExecute = false };
            foreach (var arg in new[] { "fetch", "--directory", root }) start.ArgumentList.Add(arg);
            await Run(start, ct);
            var fetched = Path.Combine(root, ".git", "FETCH_HEAD");
            CheckPath(fetched);
            if (new FileInfo(fetched).Length > 65536) return new(null, Oversized: true);
            var lines = File.ReadAllLines(fetched);
            return new(lines.Length == 1 ? lines[0].Split('\t')[0] : null);
        }

        public CheckoutRecord? Record()
        {
            if (!File.Exists(record)) return null;
            recorded = ReadFile(record, CheckoutMetadataMode);
            return ParseRecord(recorded);
        }

        /// <summary>Whether the record holds the same JSON as when it was last read or written; throws when it is missing or
        /// unsafe, is not JSON, or repeats a key.</summary>
        public bool Unchanged() => JsonNode.DeepEquals(JsonNode.Parse(recorded), JsonNode.Parse(ReadFile(record, CheckoutMetadataMode)));

        /// <summary>Replaces the record with <paramref name="value"/>.</summary>
        public void Rewrite(CheckoutRecord value)
        {
            var text = SerializeRecord(value);
            SaveFile(record, text, CheckoutMetadataMode);
            recorded = text;
        }

        public CheckoutRoot Root() => Directory.Exists(root) ? CheckoutRoot.Directory : Path.Exists(root) ? CheckoutRoot.Other : CheckoutRoot.Missing;

        public async Task Clone(RepositoryCheckout repository, CancellationToken ct) =>
            await Run(AppGitHubCommand.Create(gh, await credentials.GetCredential(ct), credentials.CredentialDirectory,
                ["repo", "clone", repository.Remote, root]), ct);

        public string? Owner()
        {
            CheckPath(ownerFile);
            return File.Exists(ownerFile) ? ReadFile(ownerFile, CheckoutMetadataMode) : null;
        }

        public void WriteOwner(string owner) => WriteNew(ownerFile, owner);

        public void WriteRecord(CheckoutRecord value)
        {
            var text = SerializeRecord(value);
            WriteNew(record + ".tmp", text);
            File.Move(record + ".tmp", record);
            recorded = text;
        }
    }

    /// <summary>The checkout's Git directory, and Git run on it with the managed environment. Git is found on first use.</summary>
    internal sealed class CheckoutFacts(string root) : ICheckoutUpdateFacts
    {
        readonly string dotGit = Path.Combine(root, ".git");
        string? git;

        Task<string> Git(CancellationToken ct, params string[] arguments) => Local(git ??= AppGitHubCommand.FindExecutable("git"), root, ct, arguments);

        public bool OwnGitDirectory()
        {
            CheckPath(root);
            CheckPath(dotGit);
            if (!Directory.Exists(dotGit)) return false;
            foreach (var name in new[] { "config", "index", "HEAD", "repoagent-owner", "objects", "refs", "logs", "info" }) CheckPath(Path.Combine(dotGit, name));
            return true;
        }

        public bool Holds(string path) => Path.Exists(Path.Combine(dotGit, path));
        public async Task<IReadOnlyList<string>> ConfigurationKeys(CancellationToken ct) =>
            Keys(await Git(ct, "config", "--no-includes", "--local", "--null", "--list"));
        public async Task<string> TopLevel(CancellationToken ct) => (await Git(ct, "rev-parse", "--show-toplevel")).TrimEnd();
        public async Task<bool> IsBare(CancellationToken ct) => Bare(await Git(ct, "rev-parse", "--is-bare-repository"));
        public async Task<IReadOnlyList<string>> OriginUrls(CancellationToken ct) =>
            Lines(await Git(ct, "config", "--get-all", "remote.origin.url"));
        public async Task<string> HeadBranch(CancellationToken ct) => (await Git(ct, "symbolic-ref", "--quiet", "HEAD")).Trim();
        public async Task<string> HeadName(CancellationToken ct) => (await Git(ct, "rev-parse", "--symbolic-full-name", "HEAD")).Trim();
        public async Task<bool> Changed(bool untracked, CancellationToken ct) =>
            await Git(ct, "status", "--porcelain=v1", untracked ? "--untracked-files=all" : "--untracked-files=no") != "";
        public async Task<IReadOnlyList<IndexEntry>> Index(CancellationToken ct) => Entries(await Git(ct, "ls-files", "-v", "-z"));
        public async Task<string> Commit(string revision, CancellationToken ct) => (await Git(ct, "rev-parse", "--verify", revision)).Trim();

        /// <summary>Forces the checkout onto <paramref name="target"/> as <paramref name="branch"/>: clears the hidden index
        /// entries' flags, checks it out over local changes, and removes untracked files that are not ignored.</summary>
        public async Task Force(string branch, string target, IReadOnlyList<string> hidden, CancellationToken ct)
        {
            if (hidden.Count != 0)
                foreach (var flag in new[] { "--no-assume-unchanged", "--no-skip-worktree" }) // Git applies one flag change per call.
                    await Git(ct, ["update-index", flag, "--", .. hidden]);
            await Git(ct, "-c", "submodule.recurse=false", "checkout", "--quiet", "--force", "--no-recurse-submodules", "-B", branch, target, "--");
            await Git(ct, "clean", "--quiet", "--force", "-d");
        }

        /// <summary>The keys in <c>git config --null --list</c> output: entries ended by NUL, each its key, a newline and its value.</summary>
        internal static IReadOnlyList<string> Keys(string listed) =>
            [.. listed.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('\n', 2)[0])];

        /// <summary>Whether <c>git rev-parse --is-bare-repository</c> output says anything but false.</summary>
        internal static bool Bare(string output) => output.Trim() != "false";

        /// <summary>The values in <c>git config --get-all</c> output, one per line.</summary>
        internal static IReadOnlyList<string> Lines(string listed) => listed.TrimEnd().Split('\n');

        /// <summary>
        /// The entries in <c>git ls-files -v -z</c> output: each ended by NUL, as its tag, a space and its path. <c>S</c> or
        /// <c>s</c> tags skip-worktree, and a lower-case tag assume-unchanged.
        /// </summary>
        internal static IReadOnlyList<IndexEntry> Entries(string listed) =>
            [.. listed.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new IndexEntry(line.Length > 2 ? line[2..] : "", char.ToUpperInvariant(line[0]) == 'S', char.IsLower(line[0])))];
    }

    static readonly string[] RecordKeys = ["version", "state", "root", "owner", "repositoryId", "repository", "remote", "branch", "commit"];

    /// <summary>
    /// The ownership record in <paramref name="text"/>. A key that is absent or holds another type reads as null, and the commit
    /// as empty when absent or null; keys this controller does not write are kept as read. Text that is not one JSON object of
    /// distinct keys, or a key or known value that is not valid text, is unreadable.
    /// </summary>
    internal static CheckoutRecord ParseRecord(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is not JsonObject read) return new() { Unreadable = true };
            // Unknown values keep their text as read: a value the writer cannot write, such as a lone surrogate, fails the
            // refresh that writes it, as 3a52528 did, and not the start.
            using var raw = JsonDocument.Parse(text);
            return new()
            {
                Version = Value<int>(read["version"]),
                State = read["state"].OptionalText(),
                Root = read["root"].OptionalText(),
                Owner = read["owner"].OptionalText(),
                RepositoryId = Value<long>(read["repositoryId"]),
                Repository = read["repository"].OptionalText(),
                Remote = read["remote"].OptionalText(),
                Branch = read["branch"].OptionalText(),
                Commit = read["commit"] is null ? "" : read["commit"].OptionalText(),
                Unknown = [.. raw.RootElement.EnumerateObject().Where(key => !RecordKeys.Contains(key.Name)).Select(key => new UnknownKey(key.Name, key.Value.GetRawText()))]
            };
        }
        catch (JsonException) { return new() { Unreadable = true }; }
        catch (ArgumentException) { return new() { Unreadable = true }; } // A key repeated in the record.
        catch (InvalidOperationException) { return new() { Unreadable = true }; } // A key or known value that is not valid text.
    }

    static T? Value<T>(JsonNode? node) where T : struct => node is JsonValue value && value.TryGetValue<T>(out var read) ? read : null;

    /// <summary>The record as its writers leave it: its keys in their order, then the keys this controller does not write.</summary>
    internal static string SerializeRecord(CheckoutRecord value)
    {
        var json = Json.Object(("version", value.Version), ("state", value.State), ("root", value.Root), ("owner", value.Owner),
            ("repositoryId", value.RepositoryId), ("repository", value.Repository), ("remote", value.Remote), ("branch", value.Branch),
            ("commit", value.Commit));
        foreach (var key in value.Unknown) json[key.Name] = JsonNode.Parse(key.Json);
        return json.ToJsonString();
    }

    public Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct) => update.Refresh(beforeApply, ct);

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
    static IOException Blocked(string reason) => CheckoutInspection.Blocked(reason);

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
            await process.WaitForExitAsync(CancellationToken.None); // Reap the killed child even when its caller cancelled.
            try { await Task.WhenAll(output, error); } catch { }
            throw;
        }
    }
    public void Dispose() => lease.Dispose();
}
