using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
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
        => await Exercise(clone, checkAuth, "");

    [Theory]
    [InlineData("metadata-id")]
    [InlineData("metadata-name")]
    [InlineData("metadata-url")]
    [InlineData("metadata-default")]
    [InlineData("unmanaged")]
    [InlineData("partial")]
    [InlineData("reuse")]
    [InlineData("executable-owner-mismatch")]
    [InlineData("record-group-readable")]
    [InlineData("marker-other-executable")]
    [InlineData("lock-group-writable")]
    [InlineData("executable-private-config")]
    [InlineData("shutdown-leases")]
    [InlineData("shutdown-forced")]
    [InlineData("trust-malformed")]
    [InlineData("dirty")]
    [InlineData("staged")]
    [InlineData("untracked")]
    [InlineData("ignored")]
    [InlineData("branch")]
    [InlineData("commit")]
    [InlineData("assume-unchanged")]
    [InlineData("skip-worktree")]
    [InlineData("operation/MERGE_HEAD")]
    [InlineData("operation/rebase-merge")]
    [InlineData("operation/sequencer")]
    [InlineData("owner")]
    [InlineData("state")]
    [InlineData("record-missing")]
    [InlineData("root-missing")]
    [InlineData("root-replaced")]
    [InlineData("marker-missing")]
    [InlineData("root-symlink")]
    [InlineData("index-symlink")]
    [InlineData("linked-git")]
    [InlineData("unsafe-config")]
    public async Task Checkout_restart_accepts_owned_local_state_and_blocks_unsafe_reuse(string scenario)
        => await Exercise(true, false, scenario);

    [Fact]
    public async Task Owner_only_executable_checkout_metadata_supports_restart()
        => await Exercise(true, false, "owner-executable-metadata");

    [Fact]
    public async Task Start_and_resume_transport_preserve_verified_cwd_and_developer_instructions()
        => await Exercise(true, false, "transport");

    [Fact]
    public async Task Production_trust_preserves_concurrent_user_config()
        => await Exercise(true, false, "trust-cas");

    [Fact]
    public async Task Production_native_loads_exact_root_trusted_project_instructions_and_skill()
        => await Exercise(true, false, "native");

    static async Task Exercise(bool clone, bool checkAuth, string scenario)
    {
        if (OperatingSystem.IsWindows()) return; // This process fixture uses the Linux shipping tools.
        var realGit = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator)
            .Select(path => Path.GetFullPath(Path.Combine(path, "git"))).First(File.Exists);
        // Git, durable files and native are real; only GitHub transport is replaced.
        using var fixture = new BrokerAuthenticationTests.Fixture(clone: clone);
        // Graceful shutdown belongs to explicit own-App mode; broker teardown always ends the tree before EOF.
        using var ownKey = scenario.StartsWith("shutdown-", StringComparison.Ordinal) ? RSA.Create(2048) : null;
        using var ownApp = ownKey is null ? null : new HttpClient(new GracefulOwnAppProvider());
        var ownKeyFile = Path.Combine(fixture.Directory, "own-app.pem");
        if (ownKey is not null) File.WriteAllText(ownKeyFile, ownKey.ExportRSAPrivateKeyPem());
        var origin = Path.Combine(fixture.Directory, "origin");
        var workspace = Path.Combine(fixture.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        await Git("init", "--quiet", "--initial-branch=trunk", origin);
        File.WriteAllText(Path.Combine(origin, "tracked-sentinel"), "approved repository\n");
        if (scenario == "native")
        {
            Directory.CreateDirectory(Path.Combine(origin, ".codex"));
            File.WriteAllText(Path.Combine(origin, ".codex", "config.toml"), "model_reasoning_effort = \"low\"\n");
            File.WriteAllText(Path.Combine(origin, "AGENTS.md"), "Use the issue64 fixture context when examining this repository.\n");
            var skill = Path.Combine(origin, ".agents", "skills", "issue64-context"); Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: issue64-context\ndescription: Inspect issue64 fixture checkout evidence.\n---\nReport the fixture checkout context.\n");
            var sibling = Path.Combine(fixture.Directory, "sibling", ".agents", "skills", "issue64-sibling"); Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "SKILL.md"), "---\nname: issue64-sibling\ndescription: Inspect the separate sibling fixture only.\n---\nUse only in its own sibling checkout.\n");
        }
        File.WriteAllText(Path.Combine(origin, ".gitignore"), "ignored-local\n");
        await Git("-C", origin, "add", ".");
        await Git("-C", origin, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "fixture");
        var commit = await Git("-C", origin, "rev-parse", "HEAD");
        var bin = Path.Combine(fixture.Directory, "tools");
        Directory.CreateDirectory(bin);
        var capture = Path.Combine(fixture.Directory, "native.jsonl");
        var metadataFile = Path.Combine(fixture.Directory, "metadata.json");
        var metadata = JsonNode.Parse("""{"id":345,"full_name":"owner/repository","clone_url":"https://github.com/owner/repository.git","default_branch":"trunk"}""")!;
        File.WriteAllText(metadataFile, metadata.ToJsonString());
        var cloneLog = Path.Combine(fixture.Directory, "clone.log");
        var native = Environment.GetEnvironmentVariable("CHECKOUT_NATIVE") ?? Environment.GetEnvironmentVariable("PATH")!
            .Split(Path.PathSeparator).Select(path => Path.Combine(path, "codex")).First(File.Exists);
        var home = Path.Combine(fixture.Directory, "private-codex");
        Directory.CreateDirectory(home);
        File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (scenario == "native" || scenario.StartsWith("trust-", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(home, "config.toml"), "model_reasoning_effort = \"medium\"\n# Preserve fixture comment\n[projects.\"/unrelated-fixture\"]\ntrust_level = \"untrusted\"\n");
            File.SetUnixFileMode(Path.Combine(home, "config.toml"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(home, "auth.json"), "{\"fixture_preservation\":\"no credentials\"}\n");
            File.WriteAllText(Path.Combine(home, "history.jsonl"), "{\"fixture_preservation\":\"not session history\"}\n");
        }
        var script = Path.Combine(bin, "codex");
        File.WriteAllText(script, """
            #!/usr/bin/python3
            import json,os,sys,subprocess,threading
            with open(os.environ['CHECKOUT_CAPTURE'],'a') as out:
             out.write(json.dumps({'cwd':os.getcwd()})+'\n')
            command=[os.environ['CHECKOUT_REAL_NATIVE']]+sys.argv[1:]
            if os.environ.get('CHECKOUT_CAS')=='1' or os.environ.get('CHECKOUT_TRANSPORT'):
             child=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,text=True)
             def output():
              for response in child.stdout:print(response,end='',flush=True)
             worker=threading.Thread(target=output);worker.start()
             for line in sys.stdin:
              request=json.loads(line);method=request.get('method')
              if os.environ.get('CHECKOUT_TRANSPORT') and method in ('thread/start','thread/resume','turn/start'):
               with open(os.environ['CHECKOUT_TRANSPORT'],'a') as log:log.write(line)
               result={'turn':{'id':'transport-turn'}} if method=='turn/start' else {'cwd':os.getcwd(),'thread':{'id':'transport-thread','cwd':os.getcwd(),'status':{'type':'idle'}}}
               if method=='turn/start':
                print(json.dumps({'method':'turn/completed','params':{'threadId':'transport-thread','turn':{'id':'transport-turn','status':'completed'}}}),flush=True)
               print(json.dumps({'id':request['id'],'result':result}),flush=True);continue
              if method=='config/value/write' and os.environ.get('CHECKOUT_CAS')=='1':
               path=os.path.join(os.environ['CODEX_HOME'],'config.toml')
               value=open(path).read().replace('\"medium\"','\"high\"')
               with open(path,'w') as config:config.write(value+'# Concurrent fixture edit\n')
              child.stdin.write(line);child.stdin.flush()
             child.stdin.close();child.wait();worker.join();sys.exit(child.returncode)
            if os.environ.get('CHECKOUT_SHUTDOWN_SIGNAL') and len(open(os.environ['CHECKOUT_CAPTURE']).readlines())==4:
             subprocess.run(command,check=True)
             if os.environ.get('CHECKOUT_SHUTDOWN_PID'):
              with open(os.environ['CHECKOUT_SHUTDOWN_PID'],'w') as pid:pid.write(str(os.getpid()))
              os.close(1)
             with open(os.environ['CHECKOUT_SHUTDOWN_SIGNAL'],'w') as signal:signal.write('shutdown')
             with open(os.environ['CHECKOUT_SHUTDOWN_RELEASE']) as release:release.read()
             sys.exit(0)
            os.execv(command[0],command)
            """);
        Executable(script);
        // Translate exactly one approved HTTPS remote. Git itself implements cloning.
        File.WriteAllText(fixture.Gh, """
            #!/usr/bin/python3
            import json,os,subprocess,sys
            a=sys.argv[1:]
            if a[:2]==['repo','clone']:
             assert a[2]=='https://github.com/owner/repository.git',a
             with open(os.environ['CHECKOUT_CLONE_LOG'],'a') as log:log.write(a[3]+'\n')
             assert os.environ['GIT_ALLOW_PROTOCOL']=='https'
             local_environment=dict(os.environ,GIT_ALLOW_PROTOCOL='file')
             subprocess.run([os.environ['CHECKOUT_REAL_GIT'],'clone',os.environ['CHECKOUT_ORIGIN'],a[3]],env=local_environment,check=True)
             subprocess.run([os.environ['CHECKOUT_REAL_GIT'],'-C',a[3],'remote','set-url','origin',a[2]],check=True)
            elif a[:2]==['api','graphql']:
             q=json.load(sys.stdin)['query']
             data={'viewer':{'login':'test-app[bot]'}} if 'query { viewer' in q else {'repository':{'discussions':{'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}}}
             if os.environ.get('CHECKOUT_DISCUSSION'):
              page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
              if 'discussions(' in q:data={'repository':{'discussions':dict(page,nodes=[json.load(open(os.environ['CHECKOUT_DISCUSSION']))])}}
              elif 'node(id:' in q:data={'node':{'comments' if 'comments(' in q else 'reactions':page}}
             print(json.dumps({'data':data}))
            elif a[:2]==['api','repos/owner/repository']:
             print(open(os.environ['CHECKOUT_METADATA']).read())
            else:raise RuntimeError('Unexpected GitHub request: '+repr(a))
            """);
        Executable(fixture.Gh);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CHECKOUT_CAPTURE"] = capture, ["CHECKOUT_ORIGIN"] = origin,
            ["CHECKOUT_TRANSPORT"] = null, ["CHECKOUT_DISCUSSION"] = null, ["CHECKOUT_CAS"] = null, ["CHECKOUT_SHUTDOWN_SIGNAL"] = null, ["CHECKOUT_SHUTDOWN_RELEASE"] = null, ["CHECKOUT_SHUTDOWN_PID"] = null,
            ["CHECKOUT_METADATA"] = metadataFile, ["CHECKOUT_CLONE_LOG"] = cloneLog,
            ["CHECKOUT_REAL_NATIVE"] = native, ["CODEX_HOME"] = home, ["CHECKOUT_REAL_GIT"] = realGit
        };
        var previous = environment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            if (clone && !checkAuth)
            {
                // Obtain the real lease and installed helpers through the no-native public entry.
                Assert.Equal(0, await fixture.Start());
                Assert.False(File.Exists(capture));
                // Exercise actual PrepareClone isolation, including its HTTPS-only parent policy.
                var hostileGit = Path.Combine(bin, "git");
                File.WriteAllText(hostileGit, "#!/bin/sh\nexit 97\n");
                Executable(hostileGit);
                var adapterRoot = Path.Combine(fixture.Directory, "adapter-control");
                var isolatedClone = AppGitHubCommand.Create(fixture.Gh, fixture.TokenFile,
                    ["repo", "clone", "https://github.com/owner/repository.git", adapterRoot]);
                Assert.Equal("https", isolatedClone.Environment["GIT_ALLOW_PROTOCOL"]);
                try { await Run(isolatedClone); }
                finally { File.Delete(hostileGit); }
                Assert.Equal("approved repository\n", File.ReadAllText(Path.Combine(adapterRoot, "tracked-sentinel")));
                Assert.Equal("https://github.com/owner/repository.git", await Git("-C", adapterRoot, "remote", "get-url", "origin"));
                Assert.Equal(commit, await Git("-C", adapterRoot, "rev-parse", "HEAD"));
                Console.WriteLine("adapter-proof actual-Create HTTPS-parent real-clone sentinel canonical-remote commit passed");
            }
            var args = new List<string> { "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
                "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
                "--workspace", workspace, "--once" };
            if (clone) args.Add("--clone");
            if (checkAuth) args.Add("--check-auth");
            if (ownKey is not null)
            {
                args.RemoveRange(args.IndexOf("--broker-url"), 4);
                args.AddRange(["--private-key", ownKeyFile]);
            }
            async Task<int> Start() => await BridgeCommand.Run(args.ToArray(), ownKey is null ? fixture.Http : ownApp, fixture.Gh);
            if (scenario.StartsWith("metadata-", StringComparison.Ordinal))
            {
                var field = scenario switch { "metadata-id" => "id", "metadata-name" => "full_name", "metadata-url" => "clone_url", _ => "default_branch" };
                metadata[field] = scenario switch { "metadata-id" => JsonValue.Create(346), "metadata-name" => JsonValue.Create("other/repository"),
                    "metadata-url" => JsonValue.Create("https://github.com/owner/repository.git?unsafe"), _ => JsonValue.Create("../trunk") };
                File.WriteAllText(metadataFile, metadata.ToJsonString());
            }
            if (scenario == "unmanaged") File.WriteAllText(Path.Combine(workspace, "user-work"), "preserve me");
            if (scenario == "partial") Directory.CreateDirectory(Path.Combine(workspace, "repository"));
            if (scenario.StartsWith("metadata-", StringComparison.Ordinal) || scenario is "unmanaged" or "partial")
            {
                var before = Snapshot(workspace);
                Assert.NotEqual(0, await Start());
                Assert.False(File.Exists(capture));
                Assert.Equal(before.Where(p => !p.Key.EndsWith(".repoagent-checkout.lock")).ToArray(),
                    Snapshot(workspace).Where(p => !p.Key.EndsWith(".repoagent-checkout.lock")).ToArray());
                Assert.Single(File.ReadAllLines(cloneLog)); // Only the independently proved adapter control cloned.
                return;
            }
            if (scenario is "trust-cas" or "trust-malformed")
            {
                var config = Path.Combine(home, "config.toml");
                if (scenario == "trust-malformed") File.WriteAllText(config, "model = [unterminated");
                var before = File.ReadAllText(config);
                if (scenario == "trust-cas") Environment.SetEnvironmentVariable("CHECKOUT_CAS", "1");
                Assert.Equal(1, await Start());
                Assert.Single(File.ReadAllLines(capture));
                Assert.Equal(scenario == "trust-cas" ? before.Replace("\"medium\"", "\"high\"") + "# Concurrent fixture edit\n" : before, File.ReadAllText(config));
                Assert.DoesNotContain("trust_level = \"trusted\"", File.ReadAllText(config));
                return;
            }
            Assert.Equal(0, await Start());
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
            if (scenario == "transport")
            {
                var transport = Path.Combine(fixture.Directory, "transport.jsonl");
                var input = Path.Combine(fixture.Directory, "discussion.json");
                var discussion = JsonNode.Parse("""{"id":"D1","number":1,"url":"https://github.com/owner/repository/discussions/1","body":"@test-app[bot] inspect fixture","bodyHTML":"fixture","title":"fixture","author":{"__typename":"User","login":"participant"},"reactionGroups":[{"content":"EYES","viewerHasReacted":true}]}""")!;
                File.WriteAllText(input, discussion.ToJsonString());
                Environment.SetEnvironmentVariable("CHECKOUT_TRANSPORT", transport);
                Environment.SetEnvironmentVariable("CHECKOUT_DISCUSSION", input);
                Assert.Equal(0, await Start());
                discussion["body"] = "@test-app[bot] updated fixture"; File.WriteAllText(input, discussion.ToJsonString());
                Assert.Equal(0, await Start());
                var requests = File.ReadAllLines(transport).Select(line => JsonNode.Parse(line)!).ToArray();
                Assert.Equal(new[] { "thread/start", "turn/start", "thread/resume", "turn/start" }, requests.Select(value => value["method"]!.GetValue<string>()));
                foreach (var request in requests.Where(value => value["method"]!.GetValue<string>().StartsWith("thread/", StringComparison.Ordinal)))
                {
                    Assert.Equal(expected, request["params"]!["cwd"]!.GetValue<string>());
                    var instructions = request["params"]!["developerInstructions"]!.GetValue<string>();
                    Assert.Contains("$github-discussion", instructions); Assert.Contains("GitHub App installation", instructions); Assert.Contains("Preserve attribution", instructions);
                }
                Assert.True(requests[2]["params"]!["excludeTurns"]!.GetValue<bool>());
                return; // Fake transport acknowledgement, never a genuine native turn or resume claim.
            }
            if (scenario is "shutdown-leases" or "shutdown-forced")
            {
                var signal = Path.Combine(fixture.Directory, "shutdown-signal");
                var release = Path.Combine(fixture.Directory, "shutdown-release");
                await Run("mkfifo", signal, release);
                Environment.SetEnvironmentVariable("CHECKOUT_SHUTDOWN_SIGNAL", signal);
                Environment.SetEnvironmentVariable("CHECKOUT_SHUTDOWN_RELEASE", release);
                var pidFile = Path.Combine(fixture.Directory, "shutdown-pid");
                if (scenario == "shutdown-forced") Environment.SetEnvironmentVariable("CHECKOUT_SHUTDOWN_PID", pidFile);
                var observed = Task.Run(() => File.ReadAllText(signal));
                var leader = Task.Run(Start);
                Assert.Equal("shutdown", await observed.WaitAsync(TimeSpan.FromSeconds(20)));
                using var rootProcess = scenario == "shutdown-forced" ? Process.GetProcessById(int.Parse(File.ReadAllText(pidFile))) : null;
                try
                {
                    if (rootProcess is not null) Assert.False(rootProcess.HasExited);
                    var count = File.ReadAllLines(capture).Length;
                    Assert.Equal(1, await Start()); // Same StateStore lease while native shutdown is held.
                    args[args.IndexOf("--state") + 1] = Path.Combine(fixture.Directory, "competing-state.json");
                    Assert.Equal(1, await Start()); // Different state, same actual workspace lease.
                    Assert.Equal(count, File.ReadAllLines(capture).Length);
                    if (rootProcess is not null)
                    {
                        Assert.False(rootProcess.HasExited); // Reader EOF does not end this real root process.
                        Console.WriteLine($"forced-shutdown root={rootProcess.Id} exited={rootProcess.HasExited}");
                        Assert.Equal(0, await leader.WaitAsync(TimeSpan.FromSeconds(10)));
                        Assert.True(rootProcess.HasExited);
                        args[args.IndexOf("--state") + 1] = fixture.State;
                        Assert.Equal(0, await Start());
                    }
                }
                finally
                {
                    if (rootProcess is null) File.WriteAllText(release, "release");
                    else if (!rootProcess.HasExited) { rootProcess.Kill(true); await rootProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                    if (rootProcess is not null) await leader.WaitAsync(TimeSpan.FromSeconds(10));
                }
                Assert.Equal(0, await leader.WaitAsync(TimeSpan.FromSeconds(10)));
                return;
            }
            if (scenario == "native")
            {
                var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
                var observation = await Run("python3", Path.Combine(testRoot, "tests/installers/test_checkout_native.py"), "--observe-production", expected, home);
                Console.WriteLine("native-production-proof " + observation);
                Assert.Equal("{\"fixture_preservation\":\"no credentials\"}\n", File.ReadAllText(Path.Combine(home, "auth.json")));
                Assert.Equal("{\"fixture_preservation\":\"not session history\"}\n", File.ReadAllText(Path.Combine(home, "history.jsonl")));
                Assert.Contains("# Preserve fixture comment", File.ReadAllText(Path.Combine(home, "config.toml")));
                return;
            }
            if (scenario.Length == 0) return;
            File.Delete(capture);
            var clones = File.ReadAllText(cloneLog);
            var record = Path.Combine(workspace, ".repoagent-checkout.json");
            var tracked = Path.Combine(expected, "tracked-sentinel");
            var dotGit = Path.Combine(expected, ".git");
            switch (scenario)
            {
                case "owner-executable-metadata":
                    foreach (var path in new[] { record, Path.Combine(workspace, ".repoagent-checkout.lock"), Path.Combine(dotGit, "repoagent-owner") })
                        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    break;
                case "executable-owner-mismatch":
                    File.WriteAllText(Path.Combine(dotGit, "repoagent-owner"), Guid.NewGuid().ToString("N"));
                    File.SetUnixFileMode(Path.Combine(dotGit, "repoagent-owner"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    break;
                case "record-group-readable": File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); break;
                case "marker-other-executable": File.SetUnixFileMode(Path.Combine(dotGit, "repoagent-owner"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute); break;
                case "lock-group-writable": File.SetUnixFileMode(Path.Combine(workspace, ".repoagent-checkout.lock"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite); break;
                case "executable-private-config": File.SetUnixFileMode(Path.Combine(home, "config.toml"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); break;
                case "reuse":
                    File.WriteAllText(Path.Combine(origin, "remote-advance"), "new remote commit");
                    await Git("-C", origin, "add", ".");
                    await Git("-C", origin, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "remote advanced");
                    break;
                case "dirty": File.AppendAllText(tracked, "local edit"); break;
                case "staged": File.AppendAllText(tracked, "staged edit"); await Git("-C", expected, "add", "."); break;
                case "untracked": File.WriteAllText(Path.Combine(expected, "local-only"), "user bytes"); break;
                case "ignored": File.WriteAllText(Path.Combine(expected, "ignored-local"), "ignored user bytes"); break;
                case "branch": await Git("-C", expected, "checkout", "-b", "local-branch"); break;
                case "commit":
                    File.AppendAllText(tracked, "local commit"); await Git("-C", expected, "add", ".");
                    await Git("-C", expected, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "local"); break;
                case "assume-unchanged": case "skip-worktree":
                    await Git("-C", expected, "update-index", "--" + scenario, "tracked-sentinel"); File.AppendAllText(tracked, "hidden local edit"); break;
                case "owner": File.WriteAllText(Path.Combine(dotGit, "repoagent-owner"), Guid.NewGuid().ToString("N")); break;
                case "state": args[args.IndexOf("--state") + 1] = Path.Combine(fixture.Directory, "other-state.json"); break;
                case "record-missing": File.Delete(record); break;
                case "root-missing": Directory.Move(expected, Path.Combine(fixture.Directory, "saved-root")); break;
                case "root-replaced":
                    Directory.Move(expected, Path.Combine(fixture.Directory, "saved-root")); await Git("clone", origin, expected);
                    await Git("-C", expected, "remote", "set-url", "origin", "https://github.com/owner/repository.git"); break;
                case "marker-missing": File.Delete(Path.Combine(dotGit, "repoagent-owner")); break;
                case "root-symlink":
                    var target = Path.Combine(fixture.Directory, "saved-root"); Directory.Move(expected, target); Directory.CreateSymbolicLink(expected, target); break;
                case "index-symlink":
                    var indexTarget = Path.Combine(fixture.Directory, "saved-index"); File.Move(Path.Combine(dotGit, "index"), indexTarget); File.CreateSymbolicLink(Path.Combine(dotGit, "index"), indexTarget); break;
                case "linked-git":
                    var gitTarget = Path.Combine(fixture.Directory, "saved-git"); Directory.Move(dotGit, gitTarget); File.WriteAllText(dotGit, "gitdir: " + gitTarget + "\n"); break;
                case "unsafe-config": await Git("-C", expected, "config", "core.fsmonitor", "/must-not-run"); break;
                default:
                    Assert.StartsWith("operation/", scenario); var marker = Path.Combine(dotGit, scenario[10..]);
                    if (scenario == "operation/MERGE_HEAD") File.WriteAllText(marker, commit + "\n"); else Directory.CreateDirectory(marker);
                    break;
            }
            var snapshot = Snapshot(workspace);
            // Local work inside the owned checkout is the idle refresh's to replace, never a reason to refuse restart.
            // This fixture has no authoritative origin lookup, so the refresh stays pending and nothing is replaced.
            var localState = scenario is "dirty" or "staged" or "untracked" or "branch" or "commit" or
                "assume-unchanged" or "skip-worktree" or "operation/MERGE_HEAD";
            var restartAllowed = localState || scenario is "reuse" or "ignored" or "owner-executable-metadata";
            Assert.Equal(restartAllowed ? 0 : 1, await Start());
            Assert.Equal(snapshot.ToArray(), Snapshot(workspace).ToArray());
            Assert.Equal(clones, File.ReadAllText(cloneLog));
            if (restartAllowed)
            {
                if (!localState) Assert.Equal(commit, await Git("-C", expected, "rev-parse", "HEAD"));
                Assert.All(File.ReadAllLines(capture), line => Assert.Equal(expected, JsonNode.Parse(line)!["cwd"]!.GetValue<string>()));
            }
            else Assert.False(File.Exists(capture));
        }
        finally { foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value); }
    }

    // Only upstream App responses are controlled; native shutdown and both ownership leases stay real.
    sealed class GracefulOwnAppProvider : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var permissions = new { discussions = "write", contents = "read" };
            object body = request.RequestUri.AbsolutePath switch
            {
                "/app" => new { id = 123, slug = "test-app" },
                "/repos/owner/repository/installation" => new { id = 234, app_id = 123, permissions },
                "/app/installations/234/access_tokens" => new
                {
                    token = "opaque-token-own-app", expires_at = DateTimeOffset.UtcNow.AddHours(1),
                    repository_selection = "selected", permissions,
                    repositories = new[] { new { id = 345, full_name = "owner/repository" } }
                },
                _ => throw new InvalidOperationException("Unexpected own-App request")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) });
        }
    }

    static SortedDictionary<string, string> Snapshot(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Visit(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(root, entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) result[relative] = "link:" + new FileInfo(entry).LinkTarget;
                else if ((attributes & FileAttributes.Directory) != 0) { result[relative] = "directory"; Visit(entry); }
                else result[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
            }
        }
        Visit(root); return result;
    }

    static void Executable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    static Task<string> Git(params string[] arguments) => Run("git", arguments);
    static Task<string> Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Run(start);
    }
    static async Task<string> Run(ProcessStartInfo start)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }
}
