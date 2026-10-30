using System.Diagnostics;

namespace RepoAgentController;

/// <summary>Dispatches gh with current App credentials, including from an already-running agent.</summary>
public static class AppGitHubCommand
{
    internal static ProcessStartInfo Create(string executable, GitHubToolToken credential, string credentialDirectory,
        IEnumerable<string> arguments)
    {
        Log.Protect(credential.Token);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        var forwarded = arguments.ToArray();
        forwarded = AppGitCredential.PrepareClone(start, credential, credentialDirectory, forwarded);
        foreach (var argument in forwarded) start.ArgumentList.Add(argument);
        IsolateEnvironment(start, Path.Combine(credentialDirectory, "gh-config"));
        // The child's environment is process memory; the token never reaches arguments or files.
        start.Environment["GH_TOKEN"] = credential.Token;
        return start;
    }

    public static async Task<int> Run(string executable, string credentialDirectory, string[] arguments, CancellationToken ct)
    {
        var credential = await CredentialHandoff.Request(credentialDirectory, ct);
        using var process = Process.Start(Create(executable, credential, credentialDirectory, arguments)) ?? throw new IOException("Could not start authenticated gh");
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return process.ExitCode;
    }

    public static string InstallWrapper(string directory, string executable, string credentialDirectory)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The App-authenticated gh wrapper requires a Linux or macOS runtime");
        GitHubAppCredentials.SecureDirectory(directory);
        var path = Path.Combine(directory, "gh");
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        var apphost = Path.Combine(AppContext.BaseDirectory, "repoagent");
        var launcher = File.Exists(apphost) ? new[] { apphost } :
            new[] { FindExecutable("dotnet"), Path.Combine(AppContext.BaseDirectory, "repoagent.dll") };
        var command = string.Join(' ', launcher.Concat(new[] {
            "github-command", "--gh-path", executable, "--credential-directory", credentialDirectory }).Select(Quote));
        File.WriteAllText(path, "#!/bin/sh\nexec " + command + " -- \"$@\"\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static void IsolateEnvironment(ProcessStartInfo start, string configurationDirectory)
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_REPO", "GH_DEBUG", "DEBUG" }) start.Environment.Remove(name);
        RepoAgent.LocalTelemetry.LocalTraceSettings.RemoveFrom(start.Environment);
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
