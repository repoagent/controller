using System.Diagnostics;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

[CollectionDefinition("Managed checkout environment", DisableParallelization = true)]
public sealed class ManagedCheckoutEnvironment;

[Collection("Managed checkout environment")]
public sealed class ManagedCheckoutTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Startup_uses_the_owned_default_branch_only_with_explicit_clone_authority(bool clone, bool checkAuth)
    {
        if (OperatingSystem.IsWindows()) return; // This process fixture uses the Linux shipping tools.
        // Git and durable files are real; only GitHub transport and native protocol are replaced.
        using var fixture = new BrokerAuthenticationTests.Fixture(clone: clone);
        var origin = Path.Combine(fixture.Directory, "origin");
        var workspace = Path.Combine(fixture.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        await Git("init", "--quiet", "--initial-branch=trunk", origin);
        File.WriteAllText(Path.Combine(origin, "tracked-sentinel"), "approved repository\n");
        await Git("-C", origin, "add", ".");
        await Git("-C", origin, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture");
        var commit = await Git("-C", origin, "rev-parse", "HEAD");
        var bin = Path.Combine(fixture.Directory, "tools");
        Directory.CreateDirectory(bin);
        var capture = Path.Combine(fixture.Directory, "native.jsonl");
        var script = Path.Combine(bin, "codex");
        File.WriteAllText(script, """
            #!/usr/bin/python3
            import json,os,sys
            with open(os.environ['CHECKOUT_CAPTURE'],'a') as out:
             out.write(json.dumps({'cwd':os.getcwd()})+'\n')
            for line in sys.stdin:
             request=json.loads(line)
             if 'id' not in request:continue
             method=request['method']
             if method=='initialize':result={}
             else:raise RuntimeError('Unexpected native request: '+method)
             print(json.dumps({'id':request['id'],'result':result}),flush=True)
            """);
        Executable(script);
        // Translate exactly one approved HTTPS remote. Git itself implements cloning.
        File.WriteAllText(fixture.Gh, """
            #!/usr/bin/python3
            import json,os,subprocess,sys
            a=sys.argv[1:]
            if a[:2]==['repo','clone']:
             assert a[2]=='https://github.com/owner/repository.git',a
             subprocess.run(['git','clone',os.environ['CHECKOUT_ORIGIN'],a[3]],check=True)
             subprocess.run(['git','-C',a[3],'remote','set-url','origin',a[2]],check=True)
            elif a[:2]==['api','graphql']:
             q=json.load(sys.stdin)['query']
             data={'viewer':{'login':'test-app[bot]'}} if 'query { viewer' in q else {'repository':{'discussions':{'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}}}
             print(json.dumps({'data':data}))
            elif a[:2]==['api','repos/owner/repository']:
             print(json.dumps({'id':345,'full_name':'owner/repository','clone_url':'https://github.com/owner/repository.git','default_branch':'trunk'}))
            else:raise RuntimeError('Unexpected GitHub request: '+repr(a))
            """);
        Executable(fixture.Gh);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CHECKOUT_CAPTURE"] = capture, ["CHECKOUT_ORIGIN"] = origin
        };
        var previous = environment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            var args = new List<string> { "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
                "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
                "--workspace", workspace, "--once" };
            if (clone) args.Add("--clone");
            if (checkAuth) args.Add("--check-auth");
            Assert.Equal(0, await BridgeCommand.Run(args.ToArray(), fixture.Http, fixture.Gh));
            Assert.NotEmpty(fixture.Transport.Requests); // Startup passed authenticated enrollment.
            if (checkAuth) { Assert.False(File.Exists(capture)); Assert.Empty(Directory.GetFileSystemEntries(workspace)); return; }
            var launches = File.ReadAllLines(capture).Select(line => JsonNode.Parse(line)!).ToArray();
            Assert.NotEmpty(launches); // A setup failure cannot masquerade as the root regression.
            var expected = clone ? Path.Combine(workspace, "repository") : workspace;
            Console.WriteLine($"checkout-proof origin={origin} commit={commit} expected={expected} tracked-sentinel={File.Exists(Path.Combine(expected, "tracked-sentinel"))} observed={launches[^1]["cwd"]}");
            Assert.All(launches, launch => Assert.Equal(expected, launch["cwd"]!.GetValue<string>()));
            if (!clone) { Assert.Empty(Directory.GetFileSystemEntries(workspace)); return; }
            Assert.Equal("approved repository\n", File.ReadAllText(Path.Combine(expected, "tracked-sentinel")));
            Assert.Equal("trunk", await Git("-C", expected, "branch", "--show-current"));
            Assert.Equal(commit, await Git("-C", expected, "rev-parse", "HEAD"));
        }
        finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    static void Executable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    static async Task<string> Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }
}
