using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace RepoAgentController;

// Only managed clone/fetch cross this interface. Ordinary local Git is not intercepted.
internal static class AppGitCredential
{
    internal static FileStream Lock(string file)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { return new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(3)) { Thread.Sleep(10); }
        }
    }

    static JsonNode Read(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length > 65536 || (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(file) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)) throw new IOException("Unavailable Git lease");
        var value = JsonNode.Parse(File.ReadAllText(file)) ?? throw new IOException("Unavailable Git lease");
        var repository = value.Text("repository");
        if (value.Text("profile") is not ("polling-clone" or "webhook-clone") ||
            repository.Split('/') is not [var owner, var name] || !Segment(owner) || !Segment(name) ||
            !DateTimeOffset.TryParse(value.Text("expiresAt"), out var expiry) || expiry <= DateTimeOffset.UtcNow.AddSeconds(30) ||
            string.IsNullOrWhiteSpace(value.Text("token")) || value.Text("token").Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new IOException("Unavailable Git lease");
        Log.Protect(value.Text("token"));
        return value;
    }
    static bool Segment(string s) => s.Length is > 0 and <= 100 && s is not ("." or "..") && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    static string Url(JsonNode lease) => "https://github.com/" + lease.Text("repository") + ".git";
    static string Quote(string s) => "'" + s.Replace("'", "'\"'\"'") + "'";
    static string Launcher()
    {
        var host = Path.Combine(AppContext.BaseDirectory, "RepoAgentController");
        return File.Exists(host) ? Quote(host) : Quote(AppGitHubCommand.FindExecutable("dotnet")) + " " + Quote(Path.Combine(AppContext.BaseDirectory, "RepoAgentController.dll"));
    }
    internal static void Install(string bin, string file)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Git wrappers require Linux or macOS runtime");
        var git = AppGitHubCommand.FindExecutable("git");
        var child = Path.Combine(Path.GetDirectoryName(file)!, "clone-bin");
        GitHubAppCredentials.SecureDirectory(bin);
        GitHubAppCredentials.SecureDirectory(child);
        Write(Path.Combine(bin, "repoagent-git"), "git-fetch", file, git);
        Write(Path.Combine(child, "git"), "git-clone-child", file, git);
        Write(Path.Combine(child, "credential"), "git-helper", file);
    }
    static void Write(string path, string operation, params string[] arguments)
    {
        File.WriteAllText(path, "#!/bin/sh\nexec " + Launcher() + " " + Quote(operation) + " " + string.Join(' ', arguments.Select(Quote)) + " \"$@\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static string[] PrepareClone(ProcessStartInfo start, string file, string[] args)
    {
        if (args.Length < 2 || args[0] != "repo" || args[1] != "clone") return args;
        var lease = Read(file);
        if (args.Length < 3) throw new ArgumentException("Clone requires the approved repository");
        var target = args[2];
        if (!string.Equals(target, lease.Text("repository"), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, Url(lease), StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, Url(lease)[..^4], StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Clone target differs from approval");
        string? destination = null;
        foreach (var arg in args.Skip(3))
        {
            if (arg == "--no-upstream") continue;
            if (destination is not null || arg.StartsWith('-')) throw new ArgumentException("Managed clone accepts only a destination");
            destination = arg;
        }
        destination = Path.GetFullPath(destination ?? lease.Text("repository").Split('/')[1]);
        var directory = Path.GetDirectoryName(file)!;
        Isolate(start, directory);
        start.Environment["REPOAGENT_CLONE_TARGET"] = destination;
        start.Environment["PATH"] = Path.Combine(directory, "clone-bin") + Path.PathSeparator + start.Environment["PATH"];
        start.WorkingDirectory = Path.Combine(directory, "git-home");
        return ["repo", "clone", Url(lease), destination, "--no-upstream"];
    }

    static void Isolate(ProcessStartInfo start, string directory)
    {
        foreach (var key in start.Environment.Keys.ToArray())
            if ((key.StartsWith("GIT_", StringComparison.Ordinal) && key != "GIT_SSL_CAINFO") || key.StartsWith("SSH_", StringComparison.Ordinal) ||
                key is "GH_TOKEN" or "GITHUB_TOKEN" or "GH_ENTERPRISE_TOKEN" or "GITHUB_ENTERPRISE_TOKEN" or "GH_DEBUG") start.Environment.Remove(key);
        var home = Path.Combine(directory, "git-home");
        GitHubAppCredentials.SecureDirectory(home);
        start.Environment["HOME"] = home;
        start.Environment["XDG_CONFIG_HOME"] = home;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_ASKPASS"] = "/bin/false";
        start.Environment["GIT_ALLOW_PROTOCOL"] = "https";
    }
    static ProcessStartInfo Git(string executable, string file, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        Isolate(start, Path.GetDirectoryName(file)!);
        var helper = Path.Combine(Path.GetDirectoryName(file)!, "clone-bin", "credential");
        foreach (var setting in new[] { "credential.helper=", "credential.helper=" + Quote(helper),
            "credential.https://github.com.helper=", "credential.https://github.com.helper=" + Quote(helper),
            "credential.useHttpPath=true", "http.extraHeader=", "http.followRedirects=false", "http.sslVerify=true", "core.askPass=/bin/false" })
        {
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(setting);
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
    static async Task<int> Execute(ProcessStartInfo start, CancellationToken ct)
    {
        using var process = Process.Start(start) ?? throw new IOException("Git process unavailable");
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return process.ExitCode;
    }
    internal static async Task<int> Run(string[] args, CancellationToken ct)
    {
        try
        {
            if (args.Length < 3) throw new ArgumentException("Invalid managed Git operation");
            var file = args[1];
            if (args[0] == "git-helper") return await Helper(file, args[2]);
            var lease = Read(file);
            var git = args[2];
            if (args[0] == "git-fetch" && args is [_, _, _, "fetch", "--directory", var directory])
            {
                directory = Path.GetFullPath(directory);
                var local = new ProcessStartInfo(git) { UseShellExecute = false, RedirectStandardOutput = true };
                Isolate(local, Path.GetDirectoryName(file)!);
                foreach (var a in new[] { "-C", directory, "config", "--null", "--list" }) local.ArgumentList.Add(a);
                using var listing = Process.Start(local) ?? throw new IOException("Git configuration unavailable");
                string config;
                using var configDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                configDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var buffer = new char[65537];
                    var length = 0;
                    while (length < buffer.Length)
                    {
                        var count = await listing.StandardOutput.ReadAsync(buffer.AsMemory(length), configDeadline.Token);
                        if (count == 0) break;
                        length += count;
                    }
                    if (length > 65536) throw new IOException("Git configuration too large");
                    config = new string(buffer, 0, length);
                    await listing.WaitForExitAsync(configDeadline.Token);
                }
                catch { if (!listing.HasExited) listing.Kill(true); throw; }
                if (listing.ExitCode != 0 || config.Length > 65536 || config.Split('\0').Any(UnsafeConfig)) throw new IOException("Repository authentication configuration is not supported");
                return await Execute(Git(git, file, "-C", directory, "fetch", "--no-recurse-submodules", "--no-tags", Url(lease)), ct);
            }
            if (args[0] == "git-clone-child")
            {
                var target = Environment.GetEnvironmentVariable("REPOAGENT_CLONE_TARGET") ?? throw new IOException("Missing clone destination");
                var forwarded = args[3..];
                if (forwarded is ["-C", var cwd, "config", "--add", "remote.origin.gh-resolved", "base"] && cwd == target)
                    return await Execute(Git(git, file, forwarded), ct);
                if (forwarded is ["-c", "credential.https://github.com.helper=", "-c", var injected, "clone", var url, var destination] &&
                    injected.StartsWith("credential.https://github.com.helper=!", StringComparison.Ordinal) &&
                    injected.EndsWith(" auth git-credential", StringComparison.Ordinal) && url == Url(lease) && destination == target)
                    return await Execute(Git(git, file, "clone", "--", url, destination), ct);
            }
            throw new ArgumentException("Unsupported managed Git invocation");
        }
        catch
        {
            // Never include input, credentials or subprocess diagnostics in helper errors.
            if (args.Length > 0 && args[0] == "git-helper") Console.Write("quit=true\n\n");
            else Console.Error.WriteLine("Managed Git authentication or invocation denied");
            return 1;
        }
    }
    static bool UnsafeConfig(string entry)
    {
        var key = entry.Split('\n', 2)[0];
        return new[] { "credential.", "http.", "url.", "include.", "includeif." }.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
            key.Equals("core.askpass", StringComparison.OrdinalIgnoreCase) || key.Equals("core.sshcommand", StringComparison.OrdinalIgnoreCase);
    }
    static async Task<int> Helper(string file, string operation)
    {
        if (operation is not ("get" or "store" or "erase")) throw new ArgumentException();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var line = new StringBuilder();
        var buffer = new char[1];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (var length = 0; ; length++)
        {
            if (length > 16384) throw new IOException();
            var count = await Console.In.ReadAsync(buffer.AsMemory(), deadline.Token);
            if (count == 0 || buffer[0] == '\n')
            {
                if (line.Length == 0) break;
                var text = line.ToString();
                var split = text.IndexOf('=');
                if (split <= 0 || !fields.TryAdd(text[..split], text[(split + 1)..])) throw new IOException();
                line.Clear();
                if (count == 0) break;
            }
            else if (buffer[0] != '\r') line.Append(buffer[0]);
        }
        using var locked = Lock(file);
        var lease = Read(file);
        if (fields.GetValueOrDefault("protocol") != "https" || fields.GetValueOrDefault("host") != "github.com" ||
            !(string.Equals(fields.GetValueOrDefault("path"), lease.Text("repository"), StringComparison.OrdinalIgnoreCase) ||
              string.Equals(fields.GetValueOrDefault("path"), lease.Text("repository") + ".git", StringComparison.OrdinalIgnoreCase))) throw new IOException();
        if (operation == "erase")
        {
            if (fields.GetValueOrDefault("password") == lease.Text("token")) File.Delete(file);
        }
        else if (operation == "get") Console.Write("username=x-access-token\npassword=" + lease.Text("token") + "\n\n");
        return 0;
    }
}
