using System.Diagnostics;
using System.Text.Json;
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
                foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "RepoAgentController.dll"), "activity", "--state", state,
                    "--discussion", "42", "--format", "json" }) start.ArgumentList.Add(argument);
                using var command = Process.Start(start)!;
                var stdout = command.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var stderr = command.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await command.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                Assert.Equal(0, command.ExitCode);
                Assert.Empty(await stderr);
                using var json = JsonDocument.Parse(await stdout);
                Assert.Equal("previous response", json.RootElement.GetProperty("snapshot").GetProperty("items")[0].GetProperty("text").GetString());

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
        }
        finally
        {
            if (stoppedViewer is not null) await stoppedViewer.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

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
