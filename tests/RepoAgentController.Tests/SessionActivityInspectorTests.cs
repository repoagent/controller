using Xunit;

namespace RepoAgentController.Tests;

public sealed class SessionActivityInspectorTests
{
    [Fact]
    public async Task Operator_reads_a_mapped_Discussions_previous_response_and_command_result_without_agent_work()
    {
        var runtime = new ActivityRuntime
        {
            Answer = new([
                new("session-a", "turn-a", "response", ActivityItemKind.Response, ActivityItemState.Completed, Text: "I checked the instructions."),
                new("session-a", "turn-a", "command", ActivityItemKind.Command, ActivityItemState.Completed,
                    Name: "cat instructions.md", Result: "Use the repository conventions.", ExitCode: 0,
                    Reads: [new("instructions.md")])], "older-page")
        };
        var inspector = Inspector(runtime);

        var page = await inspector.Read("42", ct: TestContext.Current.CancellationToken);

        Assert.Equal("session-a", page.Session.SessionId);
        Assert.Equal("I checked the instructions.", page.Items[0].Text);
        Assert.Equal("Use the repository conventions.", page.Items[1].Result);
        Assert.Equal("instructions.md", Assert.Single(page.Items[1].Reads!).Path);
        Assert.NotNull(page.NextCursor);
        Assert.False(page.Incomplete);
    }

    static SessionActivityInspector Inspector(ActivityRuntime runtime, ActivityMappings? mappings = null) =>
        new("owner/repository", "controller-lifetime", mappings ?? new(), runtime,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(), text => text);

    [Fact]
    public async Task Unmapped_foreign_dashboard_and_ambiguous_selections_never_reach_the_runtime()
    {
        var runtime = new ActivityRuntime();
        var mappings = new ActivityMappings
        {
            Entries = [new("https://github.com/owner/repository/discussions/42", "session-a"),
                new("https://github.com/owner/repository/discussions/43", "dashboard", true),
                new("https://github.com/owner/repository/discussions/44", "duplicate"),
                new("https://github.com/owner/repository/discussions/44", "other")]
        };
        var inspector = Inspector(runtime, mappings);
        foreach (var selection in new[] { "0", "41", "43", "44", "https://github.com/other/repository/discussions/42",
                     "https://github.com/owner/repository/discussions/42?ignored=true", "session-a" })
            await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => inspector.Read(selection, ct: TestContext.Current.CancellationToken));
        Assert.Equal(0, runtime.Reads);
    }

    [Fact]
    public async Task Continuation_is_bound_to_the_selected_mapping_and_controller_lifetime()
    {
        var runtime = new ActivityRuntime { Answer = new([], "provider-cursor") };
        var inspector = Inspector(runtime);
        var page = await inspector.Read("42", ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("provider-cursor", page.NextCursor);
        runtime.Answer = new([]);
        await inspector.Read("42", cursor: page.NextCursor, ct: TestContext.Current.CancellationToken);
        Assert.Equal("provider-cursor", runtime.LastQuery!.Cursor);
        runtime.ActivityEpoch++;
        await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => inspector.Read("42", cursor: page.NextCursor, ct: TestContext.Current.CancellationToken));
        Assert.Equal(2, runtime.Reads);
    }

    [Fact]
    public async Task Private_text_redacts_known_secrets_and_escapes_terminal_controls_with_explicit_truncation()
    {
        var runtime = new ActivityRuntime
        {
            Answer = new([
            new("session-a", "turn-a", "response", ActivityItemKind.Response, ActivityItemState.Completed,
                Text: "secret\u001b]52;clipboard\a" + new string('x', 40000))])
        };
        var inspector = new SessionActivityInspector("owner/repository", "lifetime", new ActivityMappings(), runtime,
            TimeProvider.System, text => text.Replace("secret", "[REDACTED]", StringComparison.Ordinal));
        var page = await inspector.Read("42", ct: TestContext.Current.CancellationToken);
        var item = Assert.Single(page.Items);
        Assert.DoesNotContain("secret", item.Text!);
        Assert.DoesNotContain('\u001b', item.Text!);
        Assert.Contains("\\u001B", item.Text!);
        Assert.True(item.Truncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(item.Text!) <= 32768);
    }

    [Fact]
    public async Task Follow_subscribes_before_history_and_reconciles_the_same_item_then_ends_on_restart()
    {
        var runtime = new ActivityRuntime();
        runtime.DuringRead = () => runtime.Report(new ActivityItemObserved(1,
            new("session-a", "turn-a", "response", ActivityItemKind.Response, ActivityItemState.Completed, Text: "finished")));
        var inspector = Inspector(runtime);
        await using var viewer = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await viewer.MoveNextAsync());
        Assert.Equal("snapshot", viewer.Current.Kind);
        Assert.Equal("finished", Assert.Single(viewer.Current.Snapshot!.Items).Text);
        runtime.Report(new ActivityRuntimeChanged(2));
        Assert.True(await viewer.MoveNextAsync());
        Assert.Equal("gap", viewer.Current.Kind);
        Assert.False(await viewer.MoveNextAsync());
    }

    [Fact]
    public async Task Slow_viewer_gets_an_explicit_gap_and_cancellation_does_not_affect_history_reads()
    {
        var runtime = new ActivityRuntime();
        var inspector = Inspector(runtime);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var viewer = inspector.Follow("42", ct: cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            Assert.True(await viewer.MoveNextAsync());
            for (var index = 0; index < 300; index++)
                runtime.Report(new ActivityItemObserved(1, new("session-a", "turn-a", $"item-{index}", ActivityItemKind.Response,
                    ActivityItemState.Completed, Text: "response")));
            Assert.True(await viewer.MoveNextAsync());
            Assert.Equal("gap", viewer.Current.Kind);
            Assert.False(await viewer.MoveNextAsync());
            await cancellation.CancelAsync();
        }
        Assert.NotNull(await inspector.Read("42", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Running_text_withholds_a_protected_suffix_until_the_complete_secret_can_be_redacted()
    {
        const string secret = "token-secret";
        var runtime = new ActivityRuntime();
        var inspector = new SessionActivityInspector("owner/repository", "lifetime", new ActivityMappings(), runtime,
            TimeProvider.System, text => text.Replace(secret, "[REDACTED]", StringComparison.Ordinal), () => secret.Length);
        await using var viewer = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await viewer.MoveNextAsync());
        runtime.Report(new ActivityItemObserved(1, new("session-a", "turn-a", "response", ActivityItemKind.Response, ActivityItemState.Running, Text: "token-")));
        Assert.True(await viewer.MoveNextAsync());
        Assert.Equal("", viewer.Current.Item!.Text);
        Assert.True(viewer.Current.Item.Incomplete);
        runtime.Report(new ActivityItemObserved(1, new("session-a", "turn-a", "response", ActivityItemKind.Response, ActivityItemState.Completed, Text: secret)));
        Assert.True(await viewer.MoveNextAsync());
        Assert.Equal("[REDACTED]", viewer.Current.Item!.Text);
    }

    [Fact]
    public async Task Timed_out_noncooperative_history_reads_keep_their_capacity_until_the_underlying_work_finishes()
    {
        var pending = new TaskCompletionSource<AgentActivityPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ActivityRuntime { ReadReply = () => pending.Task };
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var inspector = new SessionActivityInspector("owner/repository", "lifetime", new ActivityMappings(), runtime, clock, text => text);
        var first = inspector.Read("42", ct: TestContext.Current.CancellationToken);
        var second = inspector.Read("42", ct: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => inspector.Read("42", ct: TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => first);
        await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => second);
        await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => inspector.Read("42", ct: TestContext.Current.CancellationToken));
        Assert.Equal(2, runtime.Reads);
        pending.SetResult(new([]));
    }

    [Fact]
    public async Task Four_followers_are_allowed_and_disposal_releases_only_the_viewer_capacity()
    {
        var inspector = Inspector(new());
        var viewers = new List<IAsyncEnumerator<ActivityFrame>>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var viewer = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
                viewers.Add(viewer);
                Assert.True(await viewer.MoveNextAsync());
            }
            await using var overflow = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ActivityHistoryUnavailable>(async () => { await overflow.MoveNextAsync(); });
            await viewers[0].DisposeAsync();
            viewers.RemoveAt(0);
            await using var replacement = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await replacement.MoveNextAsync());
        }
        finally { foreach (var viewer in viewers) await viewer.DisposeAsync(); }
    }

    [Fact]
    public async Task A_mapping_changed_during_a_read_cannot_publish_the_old_sessions_page()
    {
        var pending = new TaskCompletionSource<AgentActivityPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ActivityRuntime { ReadReply = () => pending.Task };
        var mappings = new ActivityMappings();
        var inspector = Inspector(runtime, mappings);
        var reading = inspector.Read("42", ct: TestContext.Current.CancellationToken);
        mappings.Entries = [new("https://github.com/owner/repository/discussions/42", "session-b")];
        pending.SetResult(new([new("session-a", "turn-a", "item", ActivityItemKind.Response, ActivityItemState.Completed, Text: "old response")]));
        await Assert.ThrowsAsync<ActivityHistoryUnavailable>(() => reading);
    }

    [Fact]
    public async Task Follow_does_not_publish_queued_activity_after_the_Discussions_mapping_changes()
    {
        var runtime = new ActivityRuntime();
        var mappings = new ActivityMappings();
        var inspector = Inspector(runtime, mappings);
        await using var viewer = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await viewer.MoveNextAsync());
        mappings.Entries = [new("https://github.com/owner/repository/discussions/42", "session-b")];
        runtime.Report(new ActivityItemObserved(1, new("session-a", "turn-a", "old-session-item", ActivityItemKind.Response,
            ActivityItemState.Completed, Text: "This session is no longer mapped.")));
        Assert.True(await viewer.MoveNextAsync());
        Assert.Equal("gap", viewer.Current.Kind);
        Assert.False(await viewer.MoveNextAsync());
        Assert.Equal(1, runtime.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_runtime_reset_after_draining_updates_ends_follow_before_publishing_an_old_item(bool notify)
    {
        var runtime = new ActivityRuntime();
        var mappings = new ActivityMappings();
        var inspector = Inspector(runtime, mappings);
        await using var viewer = inspector.Follow("42", ct: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await viewer.MoveNextAsync());
        mappings.DuringRead = () =>
        {
            runtime.ActivityEpoch = 2;
            if (notify) runtime.Report(new ActivityRuntimeChanged(2));
        };
        runtime.Report(new ActivityItemObserved(1, new("session-a", "turn-a", "old-runtime-item", ActivityItemKind.Response,
            ActivityItemState.Completed, Text: "This runtime has stopped.")));
        Assert.True(await viewer.MoveNextAsync());
        Assert.Equal("gap", viewer.Current.Kind);
        Assert.False(await viewer.MoveNextAsync());
        Assert.Equal(1, runtime.Reads);
    }

    [Fact]
    public async Task Large_pages_keep_all_item_identities_and_continuation_within_the_wire_budget()
    {
        var runtime = new ActivityRuntime
        {
            Answer = new(Enumerable.Range(0, 100).Select(index => new ActivityItem("session-a", "turn-a", $"item-{index}",
                ActivityItemKind.Response, ActivityItemState.Completed, Text: new string('<', 40000))).ToArray(), "older")
        };
        var snapshot = await Inspector(runtime).Read("42", ct: TestContext.Current.CancellationToken);
        Assert.Equal(100, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.True(item.Truncated));
        Assert.NotNull(snapshot.NextCursor);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new ActivityFrame("snapshot", "lifetime", Snapshot: snapshot),
            ActivityJson.Default.ActivityFrame).Length < 1024 * 1024);
    }

    [Theory]
    [InlineData(ActivityItemKind.Omitted)]
    [InlineData(ActivityItemKind.Unsupported)]
    public async Task Omitted_and_unsupported_items_cannot_repopulate_private_fields(ActivityItemKind kind)
    {
        var runtime = new ActivityRuntime
        {
            Answer = new([new("session-a", "turn-a", "private-item", kind, ActivityItemState.Completed,
                Name: "cat auth.json", Arguments: "raw configuration", Result: "unknown private value", Text: "binary body",
                Reads: [new("auth.json")])])
        };
        var page = await Inspector(runtime).Read("42", ct: TestContext.Current.CancellationToken);
        var item = Assert.Single(page.Items);
        Assert.Null(item.Result);
        Assert.Null(item.Arguments);
        Assert.Null(item.Text);
        Assert.Null(item.Reads);
        Assert.DoesNotContain("auth.json", item.Name!);
        Assert.True(item.Incomplete);
    }

    sealed class ActivityMappings : IActivitySessions
    {
        public IReadOnlyList<ActivitySession> Entries { get; set; } = [new("https://github.com/owner/repository/discussions/42", "session-a")];
        public Action? DuringRead { get; set; }
        public Task<IReadOnlyList<ActivitySession>> ActivitySessions(CancellationToken ct)
        {
            DuringRead?.Invoke();
            return Task.FromResult(Entries);
        }
    }

    sealed class ActivityRuntime : IAgentActivity
    {
        public int ActivityEpoch { get; set; } = 1;
        public event Action<AgentActivityEvent>? ActivityChanged;
        public AgentActivityPage Answer { get; set; } = new([]);
        public int Reads { get; private set; }
        public AgentActivityQuery? LastQuery { get; private set; }
        public Action? DuringRead { get; set; }
        public Func<Task<AgentActivityPage>>? ReadReply { get; set; }
        public Task<AgentActivityPage> ReadActivity(AgentActivityQuery query, CancellationToken ct)
        {
            Reads++;
            LastQuery = query;
            DuringRead?.Invoke();
            return ReadReply?.Invoke() ?? Task.FromResult(Answer);
        }
        public void Report(AgentActivityEvent item) => ActivityChanged?.Invoke(item);
    }
}
