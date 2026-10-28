using System.Globalization;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>
/// Operator status for one controller state path. Lifetime facts are projected from this lifetime's
/// redacted log events, so every reported failure has a correlated stdout and retained-log entry.
/// Sessions and work come from the durable checkpoints whenever a snapshot is taken.
/// </summary>
public sealed class ControllerStatus : IDisposable
{
    const int MaximumFailures = 20, MaximumMessage = 2000;
    // Events that report failed or blocked work. New failure events become visible by adding their kind here.
    static readonly HashSet<string> FailureKinds = ["fatal", "poll-error", "observation-error", "dispatch-error", "delivery-retry", "receipt-error", "outbox-error",
        "app-refresh-error", "agent-request-blocked", "refresh-blocked", "webhook-error", "webhook-disable-error", "dashboard-error",
        "codex-auth-failed", "codex-auth-invalid", "codex-auth-error", "selection-error", "label-read-error"];
    static readonly HashSet<string> ActivityKinds = ["session-created", "forwarded"];
    // Events after which the saved state holds different work, such as input now waiting for its session.
    static readonly HashSet<string> WorkKinds = ["input-waiting", "input-requeued", "turn-ended"];
    readonly IStatusRecord record;
    readonly IDiscussionStore discussions;
    readonly IControllerState settings;
    readonly ICheckoutWork checkout;
    readonly TimeProvider clock;
    readonly Lock gate = new();
    LifetimeStatus lifetime;
    bool closed;

    /// <summary>Raised after recorded lifetime status or composed work changes. Handlers run on the logging thread and must not log.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts this lifetime's status in <paramref name="record"/>, composing work from the saved <paramref name="discussions"/>,
    /// the recorded <paramref name="settings"/> and the <paramref name="checkout"/>'s agent turns. Its own times, such as the
    /// start, stop and each snapshot, are <paramref name="clock"/>'s.
    /// </summary>
    public ControllerStatus(string repository, string mode, IStatusRecord record, IDiscussionStore discussions, IControllerState settings,
        ICheckoutWork checkout, TimeProvider clock)
    {
        (this.record, this.discussions, this.settings, this.checkout, this.clock) = (record, discussions, settings, checkout, clock);
        var previous = record.Read();
        var now = Time(clock.GetUtcNow());
        // Earlier failures stay visible after a restart; their timestamps place them before startedAt.
        lifetime = new(repository, new("starting") { Mode = mode, StartedAt = now, UpdatedAt = now }, new("unknown"), new("unknown"))
        { Failures = previous?.Failures ?? [], LatestActivity = previous?.LatestActivity };
        record.Write(lifetime);
    }

    public void Record(LogEntry entry)
    {
        lock (gate)
        {
            if (closed) return;
            var at = Time(entry.At);
            var item = new StatusEvent(at, entry.Kind, entry.Message.Length <= MaximumMessage ? entry.Message : entry.Message[..MaximumMessage] + " <truncated>");
            var controller = lifetime.Controller!;
            bool changed = true, notifyOnly = false;
            switch (entry.Kind)
            {
                case "ready": lifetime = lifetime with { Controller = controller with { State = "running" } }; break;
                case "fatal": lifetime = lifetime with { Controller = controller with { State = "failed" } }; break;
                case "app-token-refreshed": lifetime = lifetime with { Authentication = new("healthy") { UpdatedAt = at } }; break;
                case "app-token-throttled": lifetime = lifetime with { Authentication = new("degraded") { UpdatedAt = at, Error = item.Message } }; break;
                case "app-refresh-error": lifetime = lifetime with { Authentication = new("failed") { UpdatedAt = at, Error = item.Message } }; break;
                // Agent account credentials, separate from the GitHub App: supplied, healthy (a turn completed) or failed.
                case "codex-auth-supplied" or "codex-auth-applied": lifetime = lifetime with { CodexAuthentication = new("supplied") { UpdatedAt = at } }; break;
                case "codex-auth-verified": lifetime = lifetime with { CodexAuthentication = new("healthy") { UpdatedAt = at } }; break;
                case "codex-auth-failed": lifetime = lifetime with { CodexAuthentication = new("failed") { UpdatedAt = at, Error = item.Message } }; break;
                default: changed = false; break;
            }
            if (FailureKinds.Contains(entry.Kind)) lifetime = lifetime with { Failures = [.. lifetime.Failures.Append(item).TakeLast(MaximumFailures)] };
            else if (ActivityKinds.Contains(entry.Kind))
            {
                // The latest processed message, else its Discussion: the activity message's leading GitHub URL.
                var link = Regex.Match(entry.Message, @"(?:^|\s)latest=(https://github\.com/\S+)") is { Success: true } latest ? latest.Groups[1].Value
                    : Regex.Match(entry.Message, @"^https://github\.com/\S+") is { Success: true } leading ? leading.Value : null;
                lifetime = lifetime with { LatestActivity = item with { Url = link } };
            }
            else if (!changed)
            {
                // Work is composed from the saved state, so a work event records nothing here; consumers still refresh.
                if (!WorkKinds.Contains(entry.Kind)) return;
                notifyOnly = true;
            }
            if (!notifyOnly)
            {
                lifetime = lifetime with { Controller = lifetime.Controller! with { UpdatedAt = at } };
                record.Write(lifetime);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Records the agent sessions running a turn in this lifetime. GitHub's deletion of a Discussion forgets its record while
    /// the agent still runs its turn, so work counts that turn by its session until the agent ends it. Each call replaces the
    /// last, so calls must arrive in the order the sessions changed.
    /// </summary>
    public void Running(string[] sessions)
    {
        lock (gate)
        {
            var current = sessions.Order(StringComparer.Ordinal).ToArray();
            if (closed || lifetime.RunningSessions.SequenceEqual(current)) return;
            // Recorded only while a turn runs. Status output copies named fields only, so this stays internal.
            lifetime = lifetime with { RunningSessions = current };
            record.Write(lifetime);
        }
        Changed?.Invoke();
    }

    /// <summary>A snapshot for in-process consumers such as status publication.</summary>
    public OperatorStatus Snapshot()
    {
        LifetimeStatus copy;
        lock (gate) copy = lifetime;
        return Compose(copy, Saved(discussions).State, settings, checkout, clock.GetUtcNow());
    }

    /// <summary>
    /// The last recorded status of a running or stopped controller, read without its lease, generated at <paramref name="clock"/>'s
    /// time; null when neither a status record nor a saved Discussion state exists.
    /// </summary>
    public static OperatorStatus? Read(IStatusRecord record, IDiscussionStore discussions, IControllerState settings, ICheckoutWork checkout, TimeProvider clock)
    {
        var recorded = record.Read();
        var (state, saved) = Saved(discussions);
        return recorded is null && !saved ? null : Compose(recorded, state, settings, checkout, clock.GetUtcNow());
    }

    // Status is diagnostic: a saved state or settings record it cannot read reports no work from it rather than failing.
    static (DiscussionState? State, bool Saved) Saved(IDiscussionStore discussions)
    {
        try { return discussions.ReadDiscussions() is { } state ? (state, true) : (null, false); }
        catch (Exception error) when (error is not OperationCanceledException) { return (null, true); }
    }

    static RecordedSettings? Settings(IControllerState settings)
    {
        try { return settings.ReadSettings(); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    static OperatorStatus Compose(LifetimeStatus? recorded, DiscussionState? state, IControllerState settingsRecord, ICheckoutWork checkout, DateTimeOffset now)
    {
        // Managed clone mode also records agent turns in its refresh journal, for checkout refresh admission.
        var journal = checkout.Read();
        var settings = Settings(settingsRecord);
        var limit = settings?.Settings.GetValueOrDefault(Bridge.ActiveSessionSetting)?.Value is { } text &&
            int.TryParse(text, CultureInfo.InvariantCulture, out var number) ? number : Bridge.DefaultActiveSessions;
        // Turns end with their lifetime, so only a running controller can report one.
        var live = recorded?.Controller?.State is "starting" or "running";
        var sessions = new List<SessionStatus>();
        var held = new HashSet<string>(StringComparer.Ordinal);
        int pending = 0, retrying = 0, running = 0, started = 0, waiting = 0;
        foreach (var (_, discussion) in state is { Unreadable: null } ? state.Discussions : [])
        {
            if (discussion.Unreadable is not null) continue;
            var session = discussion.SessionId ?? "";
            if (session.Length != 0) { started++; held.Add(session); }
            var marker = discussion.Pending;
            // A failed or unacknowledged delivery waiting for its next attempt.
            var retry = discussion.Waiting?.Retry;
            // A blocked journal can describe a prior process. Only this lifetime's runtime reports prove a running turn then.
            var turn = live && (journal?.AdmissionBlocked == true
                ? session.Length != 0 && recorded!.RunningSessions.Contains(session)
                : discussion.Running is not null || discussion.UnreadableKeys.GetValueOrDefault(RecordKey.Running) is { IsObject: true } ||
                    session.Length != 0 && journal?.Sessions.Contains(session) == true);
            // A delivery or retry in a form no writer leaves reports none of its times or counts.
            SessionActivity activity;
            if (marker is not null)
            {
                pending++;
                activity = new SessionActivity.Pending(marker.AsRead is null ? Time(marker.Since) : null, marker.AsRead is null ? marker.Operation : null);
            }
            else if (retry is not null)
            {
                retrying++;
                activity = new SessionActivity.Retrying(retry.AsRead is null ? retry.Attempts : null, retry.AsRead is null ? Time(retry.After) : null);
            }
            else if (turn)
            {
                running++;
                activity = new SessionActivity.Running(discussion.Running is { } current ? Time(current.Since) : null);
            }
            else activity = new SessionActivity.Idle();
            // Observed input held in the controller until its session is idle.
            var inputs = discussion.Waiting?.Inputs.Count ?? 0;
            waiting += inputs;
            sessions.Add(new(discussion.Url, session.Length == 0 ? null : session, activity) { Waiting = inputs });
        }
        // A turn whose Discussion record was forgotten still runs, and holds its slot, until the agent ends it.
        if (live) running += recorded!.RunningSessions.Count(session => !held.Contains(session));
        if (journal?.AdmissionBlocked == true) running = live ? recorded!.RunningSessions.Count : 0;
        return new(now, state?.Repository ?? recorded?.Repository, state?.AgentLogin, recorded?.Controller ?? new("unknown"),
            recorded?.Authentication ?? new("unknown"), recorded?.CodexAuthentication ?? new("unknown"), settings,
            // Running agent turns against the effective active-session limit.
            new(started, pending, retrying, waiting, running, limit, journal?.AdmissionBlocked), sessions, recorded?.Failures ?? [], recorded?.LatestActivity)
        { FailuresAsRead = recorded?.FailuresAsRead };
    }

    static string Time(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Records the end of this lifetime; a fatal failure remains the reported state.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            var controller = lifetime.Controller!;
            var now = Time(clock.GetUtcNow());
            lifetime = lifetime with { Controller = controller with { State = controller.State != "failed" ? "stopped" : controller.State, StoppedAt = now, UpdatedAt = now } };
            record.Write(lifetime);
            closed = true;
        }
    }
}
