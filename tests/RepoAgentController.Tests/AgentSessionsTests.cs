using Xunit;

namespace RepoAgentController.Tests;

// The core's session and turn tracking in-process, over the agent runtime fake: when a session admits input, how each turn's
// lifecycle ends it, what reaches the refresh journal and in which order, and the agent account's credential health. How the
// app-server's JSON-RPC maps onto runtime events is the adapter's.
public sealed class AgentSessionsTests
{
    static readonly ModelChoice Selection = new("gpt-6-luna", "xhigh", "label");
    readonly FakeAgentRuntime runtime = new();
    readonly RecordingLog log = new();
    readonly List<string> order = [];
    readonly AgentSessions sessions;

    public AgentSessionsTests()
    {
        sessions = new(runtime, log, new Journal(order));
        sessions.TurnsChanged += running => order.Add($"turns [{string.Join(',', running)}]");
        sessions.StatusChanged += () => order.Add($"wake failure={sessions.AuthenticationFailure ?? "none"}");
    }

    Task<string> Accept(string session) =>
        sessions.Accept(session, """{"changes":[]}""", Selection, (id, turn) => order.Add($"accepted {id} {turn}"), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_completion_that_overtakes_its_acceptance_ends_the_turn_and_the_journal_records_it_before_the_acceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);
        // The reader delivers the turn's start and completion before the turn/start response reaches the request.
        runtime.Answer(request =>
        {
            runtime.Started(request.SessionId, "T1");
            runtime.Completed(request.SessionId, "T1");
            return new("T1", false);
        });

        Assert.Equal("T1", await Accept(session));

        Assert.Equal(["journal started S1 T1", "turns []", "journal completed S1 T1 completed", "wake failure=none", "turns []", "accepted S1 T1"], order);
        Assert.Equal(("native-accepted", "session=S1 model=gpt-6-luna effort=xhigh selection=label turn=T1"), log.Entries[^1]);
        // The completion already ended the turn, so the acceptance does not hold the session.
        Assert.Equal((AgentSessionStatus.Idle, 0), (sessions.Status(session), sessions.ActiveTurns));
        var (ended, running) = sessions.TakeTurns();
        Assert.Equal([new AgentSessions.TurnOutcome("S1", "T1", "completed", false)], ended);
        Assert.Empty(running);

        // A response that itself reports the turn ended holds nothing either.
        runtime.Answer(_ => new("T2", true));
        await Accept(session);
        Assert.Equal((AgentSessionStatus.Idle, 0), (sessions.Status(session), sessions.ActiveTurns));
    }

    [Fact]
    public async Task An_accepted_turn_holds_its_session_until_its_completion_even_after_the_runtime_reports_it_idle()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);
        await Accept(session);
        Assert.Equal((AgentSessionStatus.Active, 1), (sessions.Status(session), sessions.ActiveTurns));
        Assert.Equal("turns [S1]", order[^2]);

        // The pinned native reports idle before turn/completed.
        runtime.Report(session, AgentSessionStatus.Idle);
        Assert.Equal((AgentSessionStatus.Active, 1), (sessions.Status(session), sessions.ActiveTurns));
        Assert.Equal(("session-status", "session=S1 status=Idle"), log.Entries[^1]);
        Assert.Empty(sessions.TakeTurns().Ended);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Accept(session));

        runtime.Completed(session, "T1", "interrupted");
        Assert.Equal((AgentSessionStatus.Idle, 0), (sessions.Status(session), sessions.ActiveTurns));
        Assert.Equal([new AgentSessions.TurnOutcome("S1", "T1", "interrupted", false)], sessions.TakeTurns().Ended);
        Assert.Equal("T2", await Accept(session));
    }

    [Fact]
    public async Task A_lost_runtime_process_forgets_its_sessions_and_turns_without_outcomes_and_ignores_its_late_events_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);
        await Accept(session);
        order.Clear();

        runtime.Disconnect();

        Assert.Equal(["turns []", "journal disconnected", "wake failure=none"], order);
        Assert.Equal((AgentSessionStatus.Unknown, false, 0), (sessions.Status(session), sessions.Loaded(session), sessions.ActiveTurns));
        // No outcome and no running session: the bridge ends the turn as lost.
        var (ended, running) = sessions.TakeTurns();
        Assert.Empty(ended);
        Assert.Empty(running);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Accept(session));

        // A replacement process: a late completion from the lost one still reaches the journal, which filters by epoch itself.
        Assert.True(await sessions.RestartWhenIdle(() => order.Add("prepared"), ct));
        // Only a session loaded into this process takes input, whatever status the runtime reports for it.
        runtime.Report(session, AgentSessionStatus.Idle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Accept(session));
        Assert.Equal(AgentSessionStatus.Idle, await sessions.Load(session, ct));
        order.Clear();
        runtime.Completed(session, "T1", epoch: 1);
        Assert.Equal(["journal completed S1 T1 completed"], order);
        Assert.Empty(sessions.TakeTurns().Ended);
    }

    [Fact]
    public async Task A_refused_turn_leaves_its_session_ready_while_after_a_timeout_the_runtime_decides_whether_a_turn_holds_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);
        runtime.Answer(_ => throw new AgentRequestRejected("Native request failed: {\"code\":-32600}"));

        var refused = await Assert.ThrowsAsync<AgentRequestRejected>(() => Accept(session));

        // Nothing started: the admission is abandoned, the session takes its next input and is still owed its orientation.
        Assert.True(AgentSessions.Refused(refused));
        Assert.Equal((AgentSessionStatus.Idle, 0), (sessions.Status(session), sessions.ActiveTurns));
        Assert.DoesNotContain(order, entry => entry.StartsWith("accepted", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, entry => entry.Kind == "native-accepted");
        runtime.Answer(_ => throw new TimeoutException("The operation has timed out."));

        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() => Accept(session));

        // The runtime may have started a turn without answering, so the admission stays unconfirmed; its report then holds the
        // session, and defers a restart.
        Assert.False(AgentSessions.Refused(timedOut));
        Assert.Equal([true, true], runtime.Requests.Select(request => request.Orientation));
        runtime.Report(session, AgentSessionStatus.Active);
        Assert.Equal((AgentSessionStatus.Active, 1), (sessions.Status(session), sessions.ActiveTurns));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Accept(session));
        Assert.False(await sessions.RestartWhenIdle(() => order.Add("prepared"), ct));
        Assert.Equal(1, runtime.Epoch);
    }

    [Fact]
    public async Task A_turn_request_lost_with_the_runtime_process_is_not_a_refusal_and_forgets_its_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);
        // The reader reports the lost process, then fails the request still waiting for its response.
        runtime.Answer(_ =>
        {
            runtime.Disconnect();
            throw new IOException("Codex app-server closed its output");
        });

        var lost = await Assert.ThrowsAsync<IOException>(() => Accept(session));

        // The lost process may have started the turn, so unlike a refusal the admission stays unconfirmed.
        Assert.False(AgentSessions.Refused(lost));
        Assert.Equal((AgentSessionStatus.Unknown, false, 0), (sessions.Status(session), sessions.Loaded(session), sessions.ActiveTurns));
        Assert.DoesNotContain(log.Entries, entry => entry.Kind == "native-accepted");
    }

    [Fact]
    public async Task An_acceptance_the_admission_cannot_record_is_not_logged_as_accepted_while_its_turn_holds_the_session()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);

        // A refresh journal blocked by a lost process refuses to record the acceptance.
        await Assert.ThrowsAsync<IOException>(() => sessions.Accept(session, """{"changes":[]}""", Selection,
            (_, _) => throw new IOException("Checkout/native state uncertain; manual recovery required"), ct));

        Assert.DoesNotContain(log.Entries, entry => entry.Kind == "native-accepted");
        Assert.Equal((AgentSessionStatus.Active, 1), (sessions.Status(session), sessions.ActiveTurns));
    }

    [Fact]
    public async Task Each_session_receives_the_orientation_once_per_start_or_resume_and_a_report_that_overtakes_its_response_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await sessions.Start(ct);
        await Accept(session);
        runtime.Completed(session, "T1");
        await Accept(session);
        runtime.Completed(session, "T2");

        // Unloaded by the runtime: the next input resumes the session, which is oriented again.
        runtime.Report(session, AgentSessionStatus.NotLoaded);
        Assert.False(sessions.Loaded(session));
        runtime.Resumes = AgentSessionStatus.SystemError; // A failed turn's session takes its next input.
        Assert.Equal(AgentSessionStatus.SystemError, await sessions.Load(session, ct));
        Assert.Equal(("session-resumed", "S1 status=SystemError"), log.Entries[^1]);
        Assert.Equal(AgentSessionStatus.SystemError, await sessions.Load(session, ct)); // Already loaded: no second resume.
        await Accept(session);

        Assert.Equal([true, false, true], runtime.Requests.Select(request => request.Orientation));
        Assert.Equal(["S1"], runtime.Resumed);

        // A status the runtime reports while a resume or start response is in flight is newer than the response.
        runtime.Completed(session, "T3");
        runtime.Report(session, AgentSessionStatus.NotLoaded);
        runtime.Resuming = id => runtime.Report(id, AgentSessionStatus.Active);
        Assert.Equal(AgentSessionStatus.Active, await sessions.Load(session, ct));
        runtime.Report("S2", AgentSessionStatus.Active);
        Assert.Equal("S2", await sessions.Start(ct));
        Assert.Equal(AgentSessionStatus.Active, sessions.Status("S2"));
    }

    [Fact]
    public async Task A_rejected_credential_holds_input_before_dispatch_wakes_until_a_completed_turn_or_an_idle_restart_clears_it()
    {
        var ct = TestContext.Current.CancellationToken;
        sessions.TurnsChanged += _ => order.Add($"outcome held={sessions.AuthenticationFailure ?? "none"}");
        var session = await sessions.Start(ct);
        await Accept(session);
        order.Clear();

        runtime.Completed(session, "T1", "failed", "unexpected status 401 Unauthorized");

        // Held once the outcome is visible and before dispatch wakes, so input returned from the rejected turn never follows it.
        const string Rejected = "unexpected status 401 Unauthorized";
        Assert.Equal(["turns []", "outcome held=" + Rejected, "journal completed S1 T1 failed", "wake failure=" + Rejected], order);
        Assert.Equal("codex-auth-failed", log.Entries[^1].Kind);
        Assert.Equal([new AgentSessions.TurnOutcome("S1", "T1", "failed", true)], sessions.TakeTurns().Ended);

        // Another failure, or a rejection reported for a turn that did not fail, is not a credential rejection and changes
        // nothing; a running turn defers the restart.
        await Accept(session);
        runtime.Completed(session, "T2", "failed");
        await Accept(session);
        runtime.Completed(session, "T3", "interrupted", "unauthorized");
        Assert.Equal(Rejected, sessions.AuthenticationFailure);
        Assert.Equal([false, false], sessions.TakeTurns().Ended.Select(outcome => outcome.CredentialsRejected));
        await Accept(session);
        Assert.False(await sessions.RestartWhenIdle(() => order.Add("prepared"), ct));
        runtime.Completed(session, "T4", "failed", "unauthorized");
        order.Clear();

        // Replacement credentials apply at an idle restart: prepared while no process runs, then the journal's new epoch.
        Assert.True(await sessions.RestartWhenIdle(() => order.Add("prepared"), ct));
        Assert.Equal(["prepared", "journal epoch 2", "wake failure=none"], order);

        // The first completed turn verifies the credentials once; a later rejection holds again, a completed turn clears it.
        await sessions.Load(session, ct);
        await Accept(session);
        runtime.Completed(session, "T5");
        await Accept(session);
        runtime.Completed(session, "T6");
        Assert.Single(log.Entries, entry => entry == ("codex-auth-verified", "Codex account credentials authenticated a completed turn"));
        await Accept(session);
        runtime.Completed(session, "T7", "failed", "unauthorized");
        Assert.Equal("unauthorized", sessions.AuthenticationFailure);
        await Accept(session);
        runtime.Completed(session, "T8");
        Assert.Null(sessions.AuthenticationFailure);
        Assert.Equal(2, log.Entries.Count(entry => entry.Kind == "codex-auth-verified"));

        // Credentials replaced while healthy are verified again by the next completed turn.
        Assert.True(await sessions.RestartWhenIdle(() => { }, ct));
        await sessions.Load(session, ct);
        await Accept(session);
        runtime.Completed(session, "T9");
        Assert.Equal(3, log.Entries.Count(entry => entry.Kind == "codex-auth-verified"));
    }

    [Fact]
    public async Task A_turn_status_pinned_Codex_never_sends_neither_verifies_nor_rejects_the_credentials_and_keeps_its_text()
    {
        var session = await sessions.Start(TestContext.Current.CancellationToken);
        await Accept(session);

        runtime.Completed(session, "T1", "cancelled", "unauthorized");

        Assert.Null(sessions.AuthenticationFailure);
        Assert.DoesNotContain(log.Entries, entry => entry.Kind.StartsWith("codex-auth-", StringComparison.Ordinal));
        Assert.Equal([new AgentSessions.TurnOutcome("S1", "T1", "cancelled", false)], sessions.TakeTurns().Ended);
    }

    [Theory]
    [InlineData(false, "Supply fresh credentials with --codex-auth, or renew the native sign-in and restart.")]
    [InlineData(true, "Replace the --codex-auth file; the controller applies it at the next idle boundary.")]
    public async Task A_credential_rejection_names_the_remedy_for_how_the_agent_account_credentials_are_supplied(bool supplied, string remedy)
    {
        var ct = TestContext.Current.CancellationToken;
        var credentialRuntime = new FakeAgentRuntime(); // Not the one the class's sessions observe.
        var rejected = new AgentSessions(credentialRuntime, log, suppliedCredentials: supplied);
        var session = await rejected.Start(ct);
        await rejected.Accept(session, """{"changes":[]}""", Selection, null, ct);

        credentialRuntime.Completed(session, "T1", "failed", "unexpected status 401 Unauthorized");

        Assert.Equal(("codex-auth-failed", $"session=S1 turn=T1: Codex account credentials were rejected (unexpected status 401 Unauthorized). {remedy} New Discussion input is held until then."),
            log.Entries[^1]);
    }

    // The refresh journal's view of the runtime: every event and epoch change, in order.
    sealed class Journal(List<string> order) : IAgentTurnJournal
    {
        public void NewEpoch(int epoch) => order.Add($"journal epoch {epoch}");

        public void Observe(AgentRuntimeEvent change) => order.Add("journal " + change switch
        {
            AgentTurnStarted started => $"started {started.SessionId} {started.TurnId}",
            AgentTurnCompleted completed => $"completed {completed.SessionId} {completed.TurnId} {completed.ReportedStatus}",
            AgentRuntimeDisconnected => "disconnected",
            AgentSessionStatusChanged report => $"status {report.SessionId} {report.Status}",
            _ => change.ToString()
        });
    }
}
