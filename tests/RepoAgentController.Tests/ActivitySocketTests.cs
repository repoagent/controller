using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class ActivitySocketTests
{
    [Fact]
    public async Task Private_operator_socket_reads_existing_history_and_is_unavailable_after_controller_lifetime()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "activity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var state = Path.Combine(directory, "state.json");
        await File.WriteAllTextAsync(state, "{}", TestContext.Current.CancellationToken);
        IAsyncEnumerator<ActivityFrame>? stoppedViewer = null;
        try
        {
            var runtime = new Runtime();
            var inspector = new SessionActivityInspector("owner/repository", "lifetime", new Mappings(), runtime,
                TimeProvider.System, text => text);
            await using (var server = await ActivitySocket.Start(state, "owner/repository", "lifetime", inspector,
                TimeProvider.System, TestContext.Current.CancellationToken))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(ActivitySocket.Pointer(state)));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(Path.GetDirectoryName(ActivitySocket.Pointer(state))!));
                // Exercise the actual private HTTP contract, independently of the CLI client.
                using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(ActivitySocket.Pointer(state), TestContext.Current.CancellationToken));
                var socket = descriptor.RootElement.GetProperty("socket").GetString()!;
                using var handler = DirectConnection(socket);
                using var direct = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                using var body = new StringContent(JsonSerializer.Serialize(new ActivityRequest(state, "owner/repository", "lifetime", "42", 100, null, false),
                    ActivityJson.Default.ActivityRequest), Encoding.UTF8, "application/json");
                using var response = await direct.PostAsync("http://localhost/v1/activity", body, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("previous response", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
                foreach (var operation in new[] { "enable", "pair", "status", "clients", "revoke", "disable" })
                {
                    // Deliberately invalid input: authorization must precede parsing or protected reads.
                    using var deniedBody = new StringContent("not JSON", Encoding.UTF8, "application/json");
                    using var denied = await direct.PostAsync("http://localhost/v1/remote/" + operation, deniedBody, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                    var refusal = JsonSerializer.Deserialize(await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), ActivityJson.Default.RemoteAccessResult);
                    Assert.Equal(RemoteAccessOutcome.Forbidden, refusal!.Outcome);
                }
                using (var unknown = await direct.PostAsync("http://localhost/v1/remote/unknown", null, TestContext.Current.CancellationToken))
                    Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
                using (var staleBody = new StringContent(JsonSerializer.Serialize(new ActivityRequest(state, "owner/repository", "previous-lifetime", "42", 100, null, false),
                    ActivityJson.Default.ActivityRequest), Encoding.UTF8, "application/json"))
                using (var stale = await direct.PostAsync("http://localhost/v1/activity", staleBody, TestContext.Current.CancellationToken))
                    Assert.Equal("error", JsonSerializer.Deserialize((await stale.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Trim(), ActivityJson.Default.ActivityFrame)!.Kind);

                var originalDescriptor = await File.ReadAllTextAsync(ActivitySocket.Pointer(state), TestContext.Current.CancellationToken);
                try
                {
                    foreach (var protocol in new int?[] { null, 99 })
                    {
                        var incompatible = JsonNode.Parse(originalDescriptor)!.AsObject();
                        if (protocol is null) incompatible.Remove("protocol"); else incompatible["protocol"] = protocol.Value;
                        await File.WriteAllTextAsync(ActivitySocket.Pointer(state), incompatible.ToJsonString(), TestContext.Current.CancellationToken);
                        var failure = await Assert.ThrowsAsync<LocalProtocolException>(async () =>
                        {
                            await foreach (var unused in ActivityClient.Read(state, "42", 100, null, false, TestContext.Current.CancellationToken)) { }
                        });
                        Assert.Contains("matching repoagent", failure.Message, StringComparison.Ordinal);
                    }
                }
                finally { await File.WriteAllTextAsync(ActivitySocket.Pointer(state), originalDescriptor, TestContext.Current.CancellationToken); }
                var replies = new List<ActivityFrame>();
                await foreach (var reply in ActivityClient.Read(state, "42", 100, null, false, TestContext.Current.CancellationToken)) replies.Add(reply);
                Assert.Equal("previous response", Assert.Single(Assert.Single(replies).Snapshot!.Items).Text);
                await Assert.ThrowsAnyAsync<IOException>(async () =>
                {
                    await foreach (var unused in ActivityClient.Read(state, "https://github.com/other/repository/discussions/42", 100,
                        null, false, TestContext.Current.CancellationToken)) { }
                });
                var start = new ProcessStartInfo(AppGitHubCommand.FindExecutable("dotnet"))
                { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "repoagent.dll"), "activity", "--state", state,
                    "--discussion", "42", "--format", "json" }) start.ArgumentList.Add(argument);
                using var command = Process.Start(start)!;
                var stdout = command.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = command.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await command.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(0, command.ExitCode);
                Assert.Empty(await stderr);
                using var json = JsonDocument.Parse(await stdout);
                Assert.Equal("previous response", json.RootElement.GetProperty("snapshot").GetProperty("items")[0].GetProperty("text").GetString());

                // Even a root-run controller's same-UID CLI has inspection authority only.
                start.ArgumentList.Clear();
                foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "repoagent.dll"), "remote", "status", "--state", state })
                    start.ArgumentList.Add(argument);
                using var remote = Process.Start(start)!;
                var remoteOutput = remote.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var remoteError = remote.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await remote.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(1, remote.ExitCode);
                Assert.Empty(await remoteOutput);
                Assert.Contains("Forbidden", await remoteError, StringComparison.Ordinal);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                await using (var viewer = ActivityClient.Read(state, "42", 100, null, true, cancellation.Token).GetAsyncEnumerator(cancellation.Token))
                {
                    Assert.True(await viewer.MoveNextAsync());
                    Assert.Equal(1, runtime.Observers);
                    await cancellation.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await viewer.MoveNextAsync(); });
                }
                await runtime.Unsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Equal(0, runtime.Observers);
                stoppedViewer = ActivityClient.Read(state, "42", 100, null, true, TestContext.Current.CancellationToken)
                    .GetAsyncEnumerator(TestContext.Current.CancellationToken);
                Assert.True(await stoppedViewer.MoveNextAsync());
            }
            Assert.False(File.Exists(ActivitySocket.Pointer(state)));
            Assert.True(await stoppedViewer!.MoveNextAsync());
            Assert.Equal("gap", stoppedViewer.Current.Kind);
            Assert.False(await stoppedViewer.MoveNextAsync());
            await Assert.ThrowsAnyAsync<IOException>(async () =>
            {
                await foreach (var unused in ActivityClient.Read(state, "42", 100, null, false, TestContext.Current.CancellationToken)) { }
            });
            // A dead identity-matching legacy descriptor must not block an upgrade restart.
            await File.WriteAllTextAsync(ActivitySocket.Pointer(state), JsonSerializer.Serialize(
                new ActivityEndpoint(state, "owner/repository", "legacy-lifetime", Path.Combine(directory, "dead.sock")),
                ActivityJson.Default.ActivityEndpoint), TestContext.Current.CancellationToken);
            File.SetUnixFileMode(ActivitySocket.Pointer(state), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var replacement = new SessionActivityInspector("owner/repository", "replacement", new Mappings(), runtime,
                TimeProvider.System, text => text);
            await using (var restarted = await ActivitySocket.Start(state, "owner/repository", "replacement", replacement,
                TimeProvider.System, TestContext.Current.CancellationToken))
            {
                await foreach (var reply in ActivityClient.Read(state, "42", 100, null, false, TestContext.Current.CancellationToken))
                    Assert.Equal("replacement", reply.Lifetime);
            }
        }
        finally
        {
            if (stoppedViewer is not null) await stoppedViewer.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    static SocketsHttpHandler DirectConnection(string path) => new()
    {
        UseProxy = false,
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    sealed class Mappings : IActivitySessions
    {
        public Task<IReadOnlyList<ActivitySession>> ActivitySessions(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ActivitySession>>([new("https://github.com/owner/repository/discussions/42", "session")]);
    }

    sealed class Runtime : IAgentActivity
    {
        int observers;
        public int ActivityEpoch => 1;
        public int Observers => Volatile.Read(ref observers);
        public TaskCompletionSource Unsubscribed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<AgentActivityEvent>? ActivityChanged
        {
            add { Interlocked.Increment(ref observers); }
            remove { if (Interlocked.Decrement(ref observers) == 0) Unsubscribed.TrySetResult(); }
        }
        public Task<AgentActivityPage> ReadActivity(AgentActivityQuery query, CancellationToken ct) => Task.FromResult(new AgentActivityPage([
            new("session", "turn", "item", ActivityItemKind.Response, ActivityItemState.Completed, Text: "previous response")]));
    }
}
