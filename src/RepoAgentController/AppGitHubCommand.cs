using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Dispatches gh with current App credentials, including from an already-running agent.</summary>
public static class AppGitHubCommand
{
    public static ProcessStartInfo Create(string executable, string tokenFile, IEnumerable<string> arguments,
        TimeProvider? clock = null)
    {
        var credential = JsonNode.Parse(File.ReadAllText(tokenFile)) ?? throw new IOException("Missing GitHub App credential");
        var token = credential.Text("token");
        if (string.IsNullOrWhiteSpace(token) ||
            !DateTimeOffset.TryParse(credential.Text("expiresAt"), out var expiry) || expiry <= (clock ?? TimeProvider.System).GetUtcNow().AddSeconds(30))
            throw new InvalidOperationException("GitHub App credential is expired or unavailable; the bridge must refresh it before gh can run");
        Log.Protect(token);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        IsolateEnvironment(start, Path.Combine(Path.GetDirectoryName(tokenFile)!, "gh-config"));
        start.Environment["GH_TOKEN"] = token;
        return start;
    }

    public static async Task<int> Run(string executable, string tokenFile, string[] arguments, CancellationToken ct)
    {
        using var process = Process.Start(Create(executable, tokenFile, arguments)) ?? throw new IOException("Could not start authenticated gh");
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return process.ExitCode;
    }

    public static string InstallWrapper(string directory, string executable, string tokenFile)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The App-authenticated gh wrapper requires a Linux or macOS runtime");
        GitHubAppCredentials.SecureDirectory(directory);
        var path = Path.Combine(directory, "gh");
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        var apphost = Path.Combine(AppContext.BaseDirectory, "RepoAgentController");
        var launcher = File.Exists(apphost) ? new[] { apphost } :
            new[] { FindExecutable("dotnet"), Path.Combine(AppContext.BaseDirectory, "RepoAgentController.dll") };
        var command = string.Join(' ', launcher.Concat(new[] {
            "github-command", "--gh-path", executable, "--token-file", tokenFile }).Select(Quote));
        File.WriteAllText(path, "#!/bin/sh\nexec " + command + " -- \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static void IsolateEnvironment(ProcessStartInfo start, string configurationDirectory)
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_REPO", "GH_DEBUG", "DEBUG" }) start.Environment.Remove(name);
        GitHubAppCredentials.SecureDirectory(configurationDirectory);
        start.Environment["GH_CONFIG_DIR"] = configurationDirectory;
        start.Environment["GH_HOST"] = "github.com";
    }

    internal static string FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new FileNotFoundException($"Required executable '{name}' was not found on PATH");
    }
}
