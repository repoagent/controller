using System.Diagnostics;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// A managed checkout over real Git and real files, opened through the adapter. Only GitHub is replaced: a gh stand-in clones
/// a local origin and answers the repository and default-branch lookups, and a repoagent-git stand-in fetches from that origin.
/// No agent runtime runs.
/// </summary>
sealed class CheckoutFixture : IDisposable
{
    public BrokerAuthenticationTests.Fixture Broker { get; } = new(clone: true);
    public string Origin => Path.Combine(Broker.Directory, "origin");
    public string Workspace => Path.Combine(Broker.Directory, "workspace");
    public string Root => Path.Combine(Workspace, "repository");
    public string Record => Path.Combine(Workspace, ".repoagent-checkout.json");
    public string Lock => Path.Combine(Workspace, ".repoagent-checkout.lock");
    public string Marker => Path.Combine(Root, ".git", "repoagent-owner");
    public string Metadata => Path.Combine(Broker.Directory, "metadata.json");
    public string CloneLog => Path.Combine(Broker.Directory, "clone.log");
    public string Commit { get; private set; } = "";
    GitHubAppCredentials? credentials;

    public static string RealGit { get; } = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
        .Select(path => Path.GetFullPath(Path.Combine(path, "git"))).First(File.Exists);

    public async Task Start()
    {
        Directory.CreateDirectory(Workspace);
        await Git("init", "--quiet", "--initial-branch=trunk", Origin);
        File.WriteAllText(Path.Combine(Origin, "tracked-sentinel"), "approved repository\n");
        File.WriteAllText(Path.Combine(Origin, ".gitignore"), "ignored-local\n");
        Commit = await Advance("fixture");
        File.WriteAllText(Metadata, """{"id":345,"full_name":"owner/repository","clone_url":"https://github.com/owner/repository.git","default_branch":"trunk"}""");
        File.WriteAllText(Broker.Gh, $$$"""
            #!/usr/bin/python3
            import json,os,subprocess,sys
            a=sys.argv[1:]
            git,origin={{{Quote(RealGit)}}},{{{Quote(Origin)}}}
            if a[:2]==['repo','clone']:
             assert a[2]=='https://github.com/owner/repository.git',a
             assert os.environ['GIT_ALLOW_PROTOCOL']=='https'
             with open({{{Quote(CloneLog)}}},'a') as log:log.write(a[3]+'\n')
             subprocess.run([git,'clone','--quiet',origin,a[3]],env=dict(os.environ,GIT_ALLOW_PROTOCOL='file'),check=True)
             subprocess.run([git,'-C',a[3],'remote','set-url','origin',a[2]],check=True)
            elif a[:2]==['api','graphql']:
             json.load(sys.stdin);print(json.dumps({'data':{'viewer':{'login':'test-app[bot]'} } }))
            elif a[:2]==['api','repos/owner/repository']:
             print(open({{{Quote(Metadata)}}}).read())
            elif a[:2]==['api','repos/owner/repository/git/ref/heads/trunk']:
             sha=subprocess.run([git,'-C',origin,'rev-parse','refs/heads/trunk'],capture_output=True,text=True,check=True).stdout.strip()
             print(json.dumps({'ref':'refs/heads/trunk','object':{'type':'commit','sha':sha} }))
            else:raise RuntimeError('Unexpected GitHub request: '+repr(a))
            """);
        Executable(Broker.Gh);
        // The check-auth start installs the managed Git helpers, as a real clone-mode start does before it opens the checkout.
        Assert.Equal(0, await Broker.Start());
        var fetch = Path.Combine(Broker.CredentialDirectory, "bin", "repoagent-git");
        File.WriteAllText(fetch, $$"""
            #!/usr/bin/python3
            import os,subprocess,sys
            assert sys.argv[1:3]==['fetch','--directory'],sys.argv
            subprocess.run([{{Quote(RealGit)}},'-C',sys.argv[3],'fetch','--quiet',{{Quote(Origin)}},'trunk'],env=dict(os.environ,GIT_ALLOW_PROTOCOL='file'),check=True)
            """);
        Executable(fetch);
        credentials = Broker.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
    }

    public Task<ManagedCheckout> Open(string? state = null) => ManagedCheckout.Open(Workspace, state ?? Broker.State,
        new GitHub("owner/repository", credentials!, Broker.Gh), credentials!, Broker.Gh, TestContext.Current.CancellationToken);

    /// <summary>Commits a change to the origin's default branch and returns the commit.</summary>
    public async Task<string> Advance(string message)
    {
        File.WriteAllText(Path.Combine(Origin, "advanced"), message + "\n");
        await Git("-C", Origin, "add", ".");
        await Git("-C", Origin, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", message);
        return await Git("-C", Origin, "rev-parse", "HEAD");
    }

    static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    public static void Executable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Every entry under the workspace: a link's target, a directory, or a file's SHA-256.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(Workspace, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) result[relative] = "link:" + new FileInfo(entry).LinkTarget;
                else if ((attributes & FileAttributes.Directory) != 0) { result[relative] = "directory"; Visit(entry); }
                else result[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(entry)));
            }
        }
        Visit(Workspace);
        return result;
    }

    public static async Task<string> Git(params string[] arguments)
    {
        var start = new ProcessStartInfo(RealGit) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }

    public void Dispose() => Broker.Dispose();
}
