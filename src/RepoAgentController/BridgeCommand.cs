using System.CommandLine;

namespace RepoAgentController;

/// <summary>Composes the controller CLI with its outbound HTTP and gh transports.</summary>
public static class BridgeCommand
{
    public static async Task<int> Run(string[] args, HttpClient? transport = null, string? ghExecutable = null,
        TimeProvider? clock = null, CancellationToken shutdownToken = default)
    {
        var time = clock ?? TimeProvider.System;
        using var stamping = Log.Clock(time);
        if (args.Length > 0 && args[0] is "git-helper" or "git-clone-child" or "git-fetch")
            return await AppGitCredential.Run(args, CancellationToken.None);
        var repository = new Option<string>("--repo") { Required = true, Description = "GitHub owner/repository" };
        var appId = new Option<string>("--app-id") { Required = true, Description = "GitHub App ID or client ID" };
        var privateKey = new Option<FileInfo>("--private-key") { Description = "GitHub App PEM private-key file" };
        var brokerUrl = new Option<string>("--broker-url") { Description = "Enrolled HTTPS credential broker origin" };
        var enrollmentFile = new Option<FileInfo>("--enrollment-file") { Description = "Protected broker enrollment file (v1, explicit clone v2 or repository-settings v3)" };
        var enrollmentEnvironment = new Option<string>("--enrollment-environment") { Description = "Name of an environment variable holding an externally injected broker enrollment; it is removed before any child process starts" };
        var installationId = new Option<long?>("--installation-id") { Description = "Expected installation ID (otherwise discover it for --repo)" };
        var repositoryId = new Option<long?>("--repository-id") { Description = "Expected immutable repository ID for broker clone mode" };
        var mentionUser = new Option<string>("--mention-user") { Description = "Existing dedicated GitHub user to mention (without @); publication still uses the App" };
        var workspace = new Option<DirectoryInfo>("--workspace") { DefaultValueFactory = _ => new(Environment.CurrentDirectory) };
        var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
        var enroll = new Option<string[]>("--enroll") { Description = "Discussion URL to enroll (repeatable)", AllowMultipleArgumentsPerToken = true };
        var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 10, Description = "Seconds between polls" };
        var webhookUrl = new Option<string?>("--webhook-url") { Description = "Public HTTPS origin; enables repository webhooks at /github/webhook" };
        var listenPort = new Option<int>("--listen-port") { DefaultValueFactory = _ => 8080, Description = "Container HTTP listener port in webhook mode" };
        var statusPort = new Option<int>("--status-port") { DefaultValueFactory = _ => 8081, Description = "Local (loopback) port for GET /status: busy or idle and work counts, while the controller runs in polling or webhook mode" };
        var reconcileInterval = new Option<int>("--reconcile-interval") { DefaultValueFactory = _ => 300, Description = "Seconds between full reconciliations in webhook mode" };
        var disableWebhook = new Option<bool>("--disable-webhook") { Description = "Deactivate this stopped controller's saved webhook without starting agent work" };
        var once = new Option<bool>("--once") { Description = "Observe one poll, then exit (agent work may still be running)" };
        var clone = new Option<bool>("--clone") { Description = "Use explicitly approved clone-capable credentials" };
        var checkAuth = new Option<bool>("--check-auth") { Description = "Verify App installation identity and prepare authenticated gh, then exit without starting Codex" };
        var repositorySettings = new Option<bool>("--repository-settings") { Description = "Read REPOAGENT_ repository Agent variables with an explicit Agent variables: read runtime permission (direct App, or a broker enrollment with a repository-settings profile)" };
        var logFileBytes = new Option<long>("--log-file-bytes") { DefaultValueFactory = _ => RetainedLog.DefaultFileBytes, Description = $"Maximum bytes per retained log file beside --state ({RetainedLog.MinimumFileBytes}..{RetainedLog.MaximumFileBytes}); retained logs never exceed --log-files × this size" };
        var logFiles = new Option<int>("--log-files") { DefaultValueFactory = _ => RetainedLog.DefaultFiles, Description = $"Retained log files, including the active file (1..{RetainedLog.MaximumFiles}); the oldest file is deleted on rotation" };
        var dashboardCategory = new Option<string?>("--dashboard-category") { Description = "Existing Discussion category for the controller-owned status dashboard; omit to publish no dashboard" };
        var codexAuth = new Option<FileInfo>("--codex-auth") { Description = "Operator-supplied Codex account credentials (auth.json format), separate from GitHub App credentials. Installed into the private CODEX_HOME when it differs from the last supply; replacing the file while running restarts the owned Codex app-server at idle and resumes sessions" };
        var imageInventory = new Option<FileInfo?>("--image-inventory") { Description = "The running baseline image's inventory (/opt/repoagent/image-inventory.json); the dashboard compares its SHA-256 with published baseline releases" };
        var root = new RootCommand("repoagent: GitHub Discussions → agent Codex sessions, authenticated as a GitHub App") { repository, appId, privateKey, brokerUrl, enrollmentFile, enrollmentEnvironment, installationId, repositoryId, mentionUser, workspace, state, enroll, interval, webhookUrl, listenPort, statusPort, reconcileInterval, disableWebhook, once, checkAuth, clone, repositorySettings, logFileBytes, logFiles, dashboardCategory, codexAuth, imageInventory };
        var ghCredentials = new Option<DirectoryInfo>("--credential-directory") { Required = true };
        var ghPath = new Option<FileInfo>("--gh-path") { Required = true };
        var forwardedArguments = new Argument<string[]>("arguments") { Arity = ArgumentArity.ZeroOrMore };
        var githubCommand = new Command("github-command", "Run gh with the running controller's current in-memory App credential (agent wrapper entry point)") { ghCredentials, ghPath, forwardedArguments };
        githubCommand.SetAction(async (result, ct) =>
        {
            try { return await AppGitHubCommand.Run(result.GetValue(ghPath)!.FullName, result.GetValue(ghCredentials)!.FullName, result.GetValue(forwardedArguments) ?? [], ct); }
            catch (Exception error) { Log.Write("app-command-error", error.Message); return 1; }
        });
        root.Subcommands.Add(githubCommand);
        var statusState = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
        var statusCommand = new Command("status", "Print operator status as JSON: repository identity, lifetime, authentication, sessions, work and recent failures. Excludes credentials and transcript content; reads without the controller lease.") { statusState };
        statusCommand.SetAction(result =>
        {
            var path = result.GetValue(statusState)!.FullName;
            if (StatusFiles.Read(path, time) is not { } snapshot) { Console.Error.WriteLine($"No controller state or status at {path}"); return 1; }
            Console.WriteLine(OperatorStatusJson.Text(snapshot));
            return 0;
        });
        root.Subcommands.Add(statusCommand);
        ActivityCommand.Add(root);
        var recoveryState = new Option<FileInfo>("--state") { Required = true };
        var recoveryWorkspace = new Option<DirectoryInfo>("--workspace") { Required = true };
        var recoveryStopped = new Option<bool>("--confirm-runtime-stopped")
        { Description = "Attest that the previous controller, agent runtime and ALL agent-launched tools using this checkout have stopped" };
        var recoveryCommand = new Command("recover-checkout", "Explicit offline settlement of agent uncertainty; preserves evidence and waiting input, refuses malformed evidence and interrupted checkout applies")
        { recoveryState, recoveryWorkspace, recoveryStopped };
        recoveryCommand.SetAction(async (result, ct) =>
        {
            try
            {
                await CheckoutRecoveryCommand.Recover(result.GetValue(recoveryState)!.FullName, result.GetValue(recoveryWorkspace)!.FullName,
                    result.GetValue(recoveryStopped), time, ct);
                return 0;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Log.Write("checkout-recovery-refused", error.Message); return 1; }
        });
        root.Subcommands.Add(recoveryCommand);
        var logsState = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
        var logsCommand = new Command("logs", "Print retained controller logs, oldest first; they survive restarts within the configured --log-files × --log-file-bytes bound") { logsState };
        logsCommand.SetAction(result => Console.Write(RetainedLog.Read(RetainedLog.DirectoryFor(result.GetValue(logsState)!.FullName))));
        root.Subcommands.Add(logsCommand);
        root.SetAction(async (result, cancellationToken) =>
        {
            var authorityFailed = false;
            // Before anything can start a child process; validated below like an enrollment file.
            var injectedEnrollment = BrokerTokenSource.TakeInjected(result.GetValue(enrollmentEnvironment));
            using var localTracing = RepoAgent.LocalTelemetry.AgentTrace.Configure(Environment.GetEnvironmentVariable,
                typeof(BridgeCommand).Assembly.GetName().Version?.ToString() ?? "unknown");
            try
            {
                var repo = result.GetValue(repository)!;
                if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) throw new ArgumentException("--repo must be owner/repository");
                if (result.GetValue(interval) < 1) throw new ArgumentException("--interval must be positive");
                if (string.IsNullOrWhiteSpace(result.GetValue(appId))) throw new ArgumentException("--app-id must not be empty");
                if (result.GetValue(installationId) is <= 0) throw new ArgumentException("--installation-id must be positive");
                if (result.GetValue(repositoryId) is <= 0) throw new ArgumentException("--repository-id must be positive");
                if (result.GetValue(repositoryId) is not null && (!result.GetValue(clone) || result.GetValue(brokerUrl) is null))
                    throw new ArgumentException("--repository-id requires broker clone mode");
                var urlText = result.GetValue(webhookUrl);
                Uri? publicUrl = null;
                if (result.GetValue(listenPort) is < 1 or > 65535) throw new ArgumentException("--listen-port must be between 1 and 65535");
                if (result.GetValue(statusPort) is < 1 or > 65535) throw new ArgumentException("--status-port must be between 1 and 65535");
                if (result.GetValue(reconcileInterval) < 1) throw new ArgumentException("--reconcile-interval must be positive");
                if (urlText is not null && (!Uri.TryCreate(urlText, UriKind.Absolute, out publicUrl) || publicUrl.Scheme != "https" || publicUrl.UserInfo.Length != 0 ||
                    publicUrl.Query.Length != 0 || publicUrl.Fragment.Length != 0 || publicUrl.AbsolutePath != "/"))
                    throw new ArgumentException("--webhook-url must be a public HTTPS origin without a path, credentials, query or fragment; /github/webhook is added");
                if (publicUrl is not null && result.GetValue(once)) throw new ArgumentException("--once applies to polling mode only");
                if (publicUrl is not null && result.GetValue(statusPort) == result.GetValue(listenPort))
                    throw new ArgumentException("--status-port must differ from --listen-port");
                if (result.GetValue(disableWebhook) && (publicUrl is null || result.GetValue(once) || result.GetValue(checkAuth)))
                    throw new ArgumentException("--disable-webhook requires webhook mode without --once or --check-auth; stop the controller first");
                var cwd = result.GetValue(workspace)!.FullName;
                if (!Directory.Exists(cwd)) throw new ArgumentException("--workspace must exist");
                RetainedLog.Validate(result.GetValue(logFileBytes), result.GetValue(logFiles));
                var numbers = (result.GetValue(enroll) ?? []).Select(url =>
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com" || uri.Scheme != "https")
                        throw new ArgumentException("--enroll must be an https://github.com Discussion URL");
                    var parts = uri.AbsolutePath.Trim('/').Split('/');
                    if (parts.Length != 4 || !string.Equals($"{parts[0]}/{parts[1]}", repo, StringComparison.OrdinalIgnoreCase) || parts[2] != "discussions" || !int.TryParse(parts[3], out var number))
                        throw new ArgumentException("Enrollment URL must belong to --repo and end in /discussions/NUMBER");
                    return number;
                }).ToHashSet();
                var statePath = result.GetValue(state)!.FullName;
                var credentialDirectory = statePath + ".github-app";
                using var leased = new LeasedStateStore(statePath, repo, () => GitHubAppCredentials.ClearCache(credentialDirectory));
                var store = leased.Store;
                // Only the lease holder retains logs and records status for this state.
                using var retained = new RetainedLog(RetainedLog.DirectoryFor(statePath), result.GetValue(logFileBytes), result.GetValue(logFiles), time);
                using var status = StatusFiles.Open(statePath, repo, result.GetValue(checkAuth) ? "check-auth" : result.GetValue(disableWebhook) ? "disable-webhook" :
                    publicUrl is not null ? "webhook" : "polling", time);
                using var observation = Log.Observe(entry => { retained.Append(entry); status.Record(entry); });
                try
                {
                    var broker = result.GetValue(brokerUrl);
                    var enrollment = result.GetValue(enrollmentFile);
                    var injected = result.GetValue(enrollmentEnvironment);
                    var pem = result.GetValue(privateKey);
                    if (broker is not null || enrollment is not null || injected is not null)
                    {
                        if (pem is not null || broker is null || (enrollment is null) == (injected is null))
                            throw new ArgumentException("Use --broker-url with exactly one of --enrollment-file or --enrollment-environment, mutually exclusive with --private-key");
                        _ = BrokerTokenSource.ParseOrigin(broker);
                    }
                    else if (pem is null) throw new ArgumentException("Specify --private-key, or --broker-url with --enrollment-file or --enrollment-environment");
                    // Agent account credentials are checked before, and independently of, any GitHub App credential use.
                    var agentCredentials = result.GetValue(codexAuth) is { } supplied ? CodexCredentials.Open(supplied.FullName) : null;
                    using var ownedHttp = transport is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) } : null;
                    var http = transport ?? ownedHttp!;
                    var profile = new RuntimeProfile(publicUrl is not null, result.GetValue(clone), result.GetValue(repositorySettings));
                    using var credentials = broker is null
                        ? new GitHubAppCredentials(http, result.GetValue(appId)!, pem!.FullName, repo, credentialDirectory, time, profile)
                        : GitHubAppCredentials.FromBroker(http, broker, injected is null ? BrokerTokenSource.EnrollmentFile(enrollment!.FullName) : BrokerTokenSource.Injected(injected, injectedEnrollment),
                            result.GetValue(appId)!, repo, credentialDirectory, profile, time, result.GetValue(repositoryId), result.GetValue(installationId));
                    await credentials.Initialize(result.GetValue(installationId), cancellationToken);
                    var executable = ghExecutable ?? AppGitHubCommand.FindExecutable("gh");
                    var github = new GitHub(repo, credentials, executable);
                    await github.VerifyIdentity(cancellationToken);
                    await github.ConfigureMentionUser(result.GetValue(mentionUser), cancellationToken);
                    store.VerifyIdentity(credentials.AgentLogin);
                    if (result.GetValue(disableWebhook))
                        return await new GitHubWebhookRegistration(github, statePath).Deactivate() ? 0 : 1;
                    if (result.GetValue(clone)) AppGitCredential.Install(Path.Combine(credentialDirectory, "bin"), credentialDirectory);
                    var wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(credentialDirectory, "bin"), executable, credentialDirectory);
                    var settings = new RepositorySettingsRefresh(RepositorySettingsCatalogue.Settings, !result.GetValue(repositorySettings) ? null
                        : credentials.SettingsUnreadable is { } unreadable ? new UnreadableRepositorySettings(unreadable) : new GitHubRepositorySettings(github), new ControllerStateFiles(statePath), time, new ControllerLog());
                    await settings.Refresh(cancellationToken);
                    if (result.GetValue(checkAuth)) return 0;
                    agentCredentials?.Install(); // Before any owned agent process starts.
                    var discussions = new GitHubDiscussions(github);
                    await using var outbox = new GitHubOutbox(github.Repository, discussions, github.Identity, new ControllerStateFiles(statePath), time, new ControllerLog());
                    // Disposed before the outbox: a stop this lifetime handles is published, then delivery drains within a bound.
                    await using var dashboard = result.GetValue(dashboardCategory) is { } category
                        ? new ControllerDashboard(category, status, outbox,
                            new BaselineReleaseCheck(new GitHubBaselineReleases(github), ImageInventory.Sha256(result.GetValue(imageInventory)?.FullName), time, new ControllerLog(), Log.Redact),
                            time, new ControllerLog(), Log.Redact) : null;
                    using var abort = CancellationTokenSource.CreateLinkedTokenSource(credentials.BrokerCredentialLost);
                    using var failedAuthority = abort.Token.UnsafeRegister(_ => Volatile.Write(ref authorityFailed, true), null);
                    using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(abort.Token);
                    // Startup can stop immediately. Once Bridge exists, ordinary stop must leave accepted activity alive.
                    using var startupStop = cancellationToken.UnsafeRegister(_ => refreshCancellation.Cancel(), null);
                    var runtimeToken = refreshCancellation.Token;
                    using var intakeStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtimeToken);
                    var refresh = credentials.RefreshUntilCancelled(runtimeToken);
                    // Starts with the bridge, which applies each changed category setting before the next refresh.
                    var settingsRefresh = Task.CompletedTask;
                    try
                    {
                        using var checkout = result.GetValue(clone)
                            ? await ManagedCheckout.Open(cwd, statePath, github, credentials, executable, runtimeToken) : null;
                        var agentRoot = checkout?.Root ?? cwd;
                        var agentConfig = Path.Combine(credentialDirectory, "gh-config");
                        if (checkout is not null) await Codex.TrustWorkspace(agentRoot, wrapper, agentConfig, runtimeToken, credentials.BrokerCredentialLost);
                        // Read retained evidence before the constructor can archive this lifetime. Forgetting the final
                        // Discussion cannot prove prior tools stopped, so a missing journal must remain fenced.
                        var reconciliation = checkout is null ? null : new CheckoutRefresh(checkout,
                            new CheckoutBinding(checkout.Root, checkout.Owner, checkout.RepositoryId, checkout.DefaultRef), new RefreshJournalFile(statePath),
                            store.Discussions.Values.Any(record => record.HoldsSession) || Directory.Exists(statePath + ".checkout-evidence"),
                            store.Discussions.Values.Any(record => record.HoldsPending), time, new ControllerLog());
                        await using var codex = new Codex(agentRoot, wrapper, agentConfig, checkout is not null, credentials.BrokerCredentialLost,
                            suppliedCredentials: agentCredentials is not null);
                        var sessions = new AgentSessions(codex, new ControllerLog(), reconciliation, credentials.BrokerCredentialLost, agentCredentials is not null);
                        // Status counts a turn by its session too, so it stays running after its Discussion record is forgotten.
                        sessions.TurnsChanged += status.Running;
                        await using var managedRefresh = reconciliation;
                        await codex.Initialize(runtimeToken);
                        if (reconciliation is not null) await reconciliation.Start(sessions,
                            TimeSpan.FromSeconds(publicUrl is null ? result.GetValue(interval) : result.GetValue(reconcileInterval)), intakeStop.Token);
                        var bridge = new Bridge(github.Repository, discussions, github.Identity, sessions, store, outbox, numbers, settings, new AgentInputJson(), new ControllerLog(),
                            time, reconciliation);
                        if (!result.GetValue(once)) startupStop.Dispose();
                        try
                        {
                            // Offline recovery must inspect original pending evidence before it can be normalized away.
                            if (reconciliation?.AdmissionBlocked != true) bridge.ResumeInterrupted();
                            settingsRefresh = settings.RefreshUntilCancelled(bridge.ReconcileCategoryEnrollment, intakeStop.Token);
                            dashboard?.Start(runtimeToken, codex);
                            // Replaced agent credentials apply between dispatches at a global idle boundary: the checkout journal's in
                            // clone mode, else the agent's. Dispatch holds its gate before refresh admission, so this order matches.
                            await using var agentRefresh = agentCredentials?.Watch(bridge.ReplaceAgentCredentials, time, abort.Cancel, intakeStop.Token);
                            if (!result.GetValue(once)) outbox.Start(runtimeToken);
                            using var dispatchStop = CancellationTokenSource.CreateLinkedTokenSource(intakeStop.Token);
                            var dispatcher = bridge.DispatchWhenIdle(dispatchStop.Token, runtimeToken);
                            try
                            {
                                using var webhooks = publicUrl is null ? null : new DiscussionWebhooks(github, bridge, new GitHubWebhookRegistration(github, statePath),
                                    new WebhookConfiguration(publicUrl, result.GetValue(listenPort), TimeSpan.FromSeconds(result.GetValue(reconcileInterval))), time, reconciliation);
                                // A long-running controller reports busy or idle in every mode; --once observes once and exits.
                                // Disposed after the webhook is deactivated: the HTTP server stops last.
                                await using var server = result.GetValue(once) ? null : await ControllerHttpServer.Start(result.GetValue(statusPort),
                                    () => StatusSnapshot.From(status.Snapshot(), managedCheckoutExpected: reconciliation is not null),
                                    webhooks is null ? null : (result.GetValue(listenPort), webhooks.Receive), runtimeToken);
                                var activityLifetime = Guid.NewGuid().ToString("N");
                                var inspector = new SessionActivityInspector(github.Repository, activityLifetime, bridge, codex, time,
                                    Log.Redact, () => Log.ProtectedLength);
                                await using var activity = result.GetValue(once) ? null : await ActivitySocket.TryStart(statePath, github.Repository,
                                    inspector, activityLifetime, time, runtimeToken);
                                if (webhooks is not null)
                                {
                                    await webhooks.Run(intakeStop.Token, runtimeToken);
                                    return 0;
                                }
                                Log.Write("mode", $"polling interval-seconds={result.GetValue(interval)}");
                                Log.Write("ready", $"repository={repo} actor={credentials.AgentLogin} mode=polling");
                                return await new ControllerPolling(bridge, outbox, time, new ControllerLog(), TimeSpan.FromSeconds(result.GetValue(interval)))
                                    .Run(result.GetValue(once), intakeStop.Token, runtimeToken);
                            }
                            finally
                            {
                                await dispatchStop.CancelAsync();
                                await dispatcher;
                            }
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !abort.IsCancellationRequested)
                        { return 0; }
                        catch
                        {
                            await abort.CancelAsync();
                            throw;
                        }
                        finally
                        {
                            await intakeStop.CancelAsync();
                            if (!result.GetValue(once) && cancellationToken.IsCancellationRequested && !abort.IsCancellationRequested)
                            {
                                Log.Write("shutdown-draining", "New input is held while agent work finishes");
                                await bridge.StopAndDrain(abort.Token);
                            }
                        }
                    }
                    catch (Exception error) when (dashboard is not null && !cancellationToken.IsCancellationRequested)
                    {
                        dashboard.Failed(error.Message);
                        throw;
                    }
                    finally
                    {
                        await refreshCancellation.CancelAsync();
                        await settingsRefresh;
                        await refresh;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested || Volatile.Read(ref authorityFailed))
                {
                    Log.Write("fatal", error.Message); // While leased, so status and retained logs record it.
                    return 1;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !Volatile.Read(ref authorityFailed)) { return 0; }
            catch (Exception error) { Log.Write("fatal", error.Message); return 1; }
        });
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = Timeout.InfiniteTimeSpan }, shutdownToken);
    }
}
