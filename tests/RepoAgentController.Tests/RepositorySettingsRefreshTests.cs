using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

// RepositorySettingsRefresh in-process, with the controller's own catalogue: the core's decisions over what GitHub reports for each
// REPOAGENT_ variable, what it records for status and the next lifetime, and what it logs.
public sealed class RepositorySettingsRefreshTests
{
    const string Setting = "REPOAGENT_MAX_ACTIVE_SESSIONS";
    const string Categories = "REPOAGENT_ENROLLMENT_CATEGORIES";
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly FakeTimeProvider clock = new(Start);
    readonly InMemoryRepositorySettings github = new();
    readonly InMemoryControllerState state = new();
    readonly RecordingLog log = new();

    RepositorySettingsRefresh Lifetime(TimeProvider? time = null) => new(RepositorySettingsCatalogue.Settings, github, state, time ?? clock, log);

    // The repository-setting lines logged for one setting, without the kind.
    List<string> Reported(string name = Setting) =>
        log.Entries.Where(entry => entry.Kind == "repository-setting" && entry.Message.StartsWith($"name={name} ", StringComparison.Ordinal))
            .Select(entry => entry.Message).ToList();

    [Theory]
    [InlineData(null, null, "state=absent")]
    [InlineData("4", 403, "state=read-failed reason=\"HTTP 403\"")]
    [InlineData("4", 502, "state=read-failed reason=\"HTTP 502\"")]
    [InlineData(null, 401, "state=read-failed reason=\"HTTP 401\"")]
    public async Task Confirmed_absence_is_reported_separately_from_a_failed_read(string? value, int? status, string reported)
    {
        github.Set(Setting, value);
        if (status is not null) github.Fail(Setting, new RepositorySettingRejectedException(status.Value));
        var settings = Lifetime();

        Assert.True(await settings.Refresh(TestContext.Current.CancellationToken));

        Assert.Equal([$"name={Setting} {reported}"], Reported());
        Assert.Null(settings[Setting].Value);
    }

    [Fact]
    public async Task A_read_that_GitHub_never_answered_is_a_failed_read_without_an_HTTP_status()
    {
        github.Set(Setting, "4");
        github.Fail(Setting, new IOException("gh failed: connection reset"));
        var settings = Lifetime();

        await settings.Refresh(TestContext.Current.CancellationToken);

        Assert.Equal([$"name={Setting} state=read-failed reason=\"GitHub read failed\""], Reported());
        Assert.Equal(SettingState.ReadFailed, settings[Setting].State);
    }

    [Fact]
    public async Task Settings_the_runtime_cannot_read_keep_the_recorded_value_and_report_why()
    {
        state.WriteSettings(new(new Dictionary<string, SettingValue> { [Setting] = new(SettingState.Configured, "6") { Since = Start } }));
        github.Set(Setting, "4");
        github.Fail(Setting, new RepositorySettingsUnreadableException("the App installation does not grant Agent variables: read"));
        var settings = Lifetime();

        await settings.Refresh(TestContext.Current.CancellationToken);

        Assert.Equal([$"name={Setting} state=read-failed value=6 reason=\"the App installation does not grant Agent variables: read\""], Reported());
        Assert.Equal(new SettingValue(SettingState.ReadFailed, "6", "the App installation does not grant Agent variables: read") { Since = Start },
            settings[Setting]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("-1")]
    [InlineData("four")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task An_invalid_value_is_reported_and_never_consumed(string value)
    {
        github.Set(Setting, value);
        var settings = Lifetime();

        await settings.Refresh(TestContext.Current.CancellationToken);

        Assert.Equal([$"name={Setting} state=invalid reason=\"must be an integer from 1 to 64\""], Reported());
        // Since is when the effective value began; no value applies before or after.
        Assert.Equal(new SettingValue(SettingState.Invalid, null, "must be an integer from 1 to 64"), settings[Setting]);
    }

    [Theory]
    [InlineData(" Agents ,Q&A,  Show and tell ", "state=configured value=Agents, Q&A, Show and tell")]
    [InlineData("", Invalid)]
    [InlineData("Agents,,Q&A", Invalid)]
    [InlineData("Agents, agents", Invalid)]
    [InlineData("Agents\nQ&A", Invalid)]
    [InlineData("A,B,C,D,E,F,G,H,I,J,K,L,M,N,O,P,Q,R,S,T,U,V,W,X,Y,Z", Invalid)]
    public async Task Category_names_are_a_normalized_comma_separated_list_and_anything_else_is_never_consumed(string value, string reported)
    {
        github.Set(Categories, value);
        var settings = Lifetime();

        await settings.Refresh(TestContext.Current.CancellationToken);

        Assert.Equal([$"name={Categories} {reported}"], Reported(Categories));
        Assert.Equal(reported == Invalid ? null : "Agents, Q&A, Show and tell", settings[Categories].Value);
    }

    const string Invalid = "state=invalid reason=\"must list 1 to 25 distinct Discussion category names separated by commas, each at most 100 characters\"";

    [Fact]
    public async Task An_operator_change_applies_at_the_next_refresh_and_an_unusable_read_keeps_the_last_effective_value()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = Lifetime();
        var changes = 0;
        settings.Changed += () => changes++;
        github.Set(Setting, "4");
        Assert.True(await settings.Refresh(ct));

        clock.Advance(TimeSpan.FromMinutes(1));
        github.Set(Setting, "6");
        Assert.True(await settings.Refresh(ct));
        var six = Start.AddMinutes(1);
        Assert.Equal(new SettingValue(SettingState.Configured, "6") { Since = six }, settings[Setting]);

        clock.Advance(TimeSpan.FromMinutes(1));
        github.Set(Setting, "unlimited");
        Assert.True(await settings.Refresh(ct));
        // The retained value keeps the time it was first observed.
        Assert.Equal(new SettingValue(SettingState.Invalid, "6", "must be an integer from 1 to 64") { Since = six }, settings[Setting]);

        clock.Advance(TimeSpan.FromMinutes(1));
        github.Fail(Setting, new RepositorySettingRejectedException(503));
        Assert.True(await settings.Refresh(ct));
        Assert.Equal(new SettingValue(SettingState.ReadFailed, "6", "HTTP 503") { Since = six }, settings[Setting]);

        clock.Advance(TimeSpan.FromMinutes(1));
        github.Set(Setting, null);
        Assert.True(await settings.Refresh(ct));
        Assert.Equal(new SettingValue(SettingState.Absent, null) { Since = Start.AddMinutes(4) }, settings[Setting]);

        // An unchanged read changes nothing and logs nothing.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await settings.Refresh(ct));

        Assert.Equal([
            $"name={Setting} state=configured value=4",
            $"name={Setting} state=configured value=6",
            $"name={Setting} state=invalid value=6 reason=\"must be an integer from 1 to 64\"",
            $"name={Setting} state=read-failed value=6 reason=\"HTTP 503\"",
            $"name={Setting} state=absent"], Reported());
        Assert.Equal(5, changes);
        Assert.Equal(6, github.Reads.Count(read => read == Setting));
    }

    [Fact]
    public async Task The_refresh_loop_reads_once_a_minute_and_applies_each_change_before_the_next_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new SteppedClock(Start);
        var settings = Lifetime(time);
        var catalogue = RepositorySettingsCatalogue.Settings.Count;
        github.Set(Setting, "4");
        await settings.Refresh(ct);
        var applied = new List<string?>();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = settings.RefreshUntilCancelled(_ =>
        {
            applied.Add(settings[Setting].Value);
            called.TrySetResult();
            return release.Task;
        }, stop.Token);
        await time.Armed();

        github.Set(Setting, "6");
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(catalogue, github.Reads.Count);
        time.Advance(TimeSpan.FromSeconds(1));
        await called.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal(["6"], applied);

        // Until the change is applied no further refresh is due, however much time passes.
        github.Set(Setting, "8");
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(time.WasArmed());
        Assert.Equal(2 * catalogue, github.Reads.Count);

        release.SetResult();
        await time.Armed();
        time.Advance(TimeSpan.FromMinutes(1));
        await time.Armed();
        Assert.Equal(["6", "8"], applied);

        // A refresh that changes nothing applies nothing.
        time.Advance(TimeSpan.FromMinutes(1));
        await time.Armed();
        Assert.Equal(["6", "8"], applied);
        Assert.Equal(4 * catalogue, github.Reads.Count);

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }

    [Fact]
    public async Task After_a_restart_a_failed_or_invalid_read_keeps_the_recorded_value_and_records_the_problem()
    {
        var ct = TestContext.Current.CancellationToken;
        github.Set(Setting, "6");
        await Lifetime().Refresh(ct);
        Assert.Equal(new SettingValue(SettingState.Configured, "6") { Since = Start }, state.ReadSettings()!.Settings[Setting]);

        // A later lifetime starts from the recorded value, then cannot read the variable: the earlier value still applies.
        clock.Advance(TimeSpan.FromHours(1));
        github.Fail(Setting, new RepositorySettingRejectedException(503));
        var unreadable = Lifetime();
        Assert.Equal(new SettingValue(SettingState.Pending, "6") { Since = Start }, unreadable[Setting]);
        await unreadable.Refresh(ct);
        var failed = new SettingValue(SettingState.ReadFailed, "6", "HTTP 503") { Since = Start };
        Assert.Equal(failed, unreadable[Setting]);
        Assert.Equal(failed, state.ReadSettings()!.Settings[Setting]);

        clock.Advance(TimeSpan.FromHours(1));
        github.Set(Setting, "unlimited");
        var invalid = Lifetime();
        await invalid.Refresh(ct);
        var recorded = state.ReadSettings()!.Settings;
        Assert.Equal(new SettingValue(SettingState.Invalid, "6", "must be an integer from 1 to 64") { Since = Start }, recorded[Setting]);
        Assert.DoesNotContain(recorded.Values, value => value.Value == "unlimited" || value.Reason?.Contains("unlimited") == true); // Never echoed.
        Assert.Equal([
            $"name={Setting} state=configured value=6",
            $"name={Setting} state=read-failed value=6 reason=\"HTTP 503\"",
            $"name={Setting} state=invalid value=6 reason=\"must be an integer from 1 to 64\""], Reported());
    }

    [Fact]
    public async Task A_recorded_value_the_catalogue_no_longer_accepts_is_not_retained()
    {
        state.WriteSettings(new(new Dictionary<string, SettingValue> { [Setting] = new(SettingState.Configured, "99") { Since = Start } }));
        github.Fail(Setting, new RepositorySettingRejectedException(503));
        var settings = Lifetime();
        Assert.Equal(new SettingValue(SettingState.Pending, null), settings[Setting]);

        await settings.Refresh(TestContext.Current.CancellationToken);

        Assert.Equal([$"name={Setting} state=read-failed reason=\"HTTP 503\""], Reported());
    }

    [Fact]
    public async Task Without_repository_settings_nothing_is_read_and_every_setting_is_recorded_disabled()
    {
        state.WriteSettings(new(new Dictionary<string, SettingValue> { [Setting] = new(SettingState.Configured, "6") { Since = Start } }));
        var settings = new RepositorySettingsRefresh(RepositorySettingsCatalogue.Settings, null, state, clock, log);

        Assert.False(await settings.Refresh(TestContext.Current.CancellationToken));

        Assert.Equal([("repository-settings", "state=disabled reason=\"start with --repository-settings and Agent variables: read to use REPOAGENT_ variables\"")],
            log.Entries);
        Assert.Empty(github.Reads);
        Assert.All(RepositorySettingsCatalogue.Settings, setting => Assert.Equal(new SettingValue(SettingState.Disabled, null), settings[setting.Name]));
        Assert.Equal(RepositorySettingsCatalogue.Settings.Select(setting => (setting.Name, new SettingValue(SettingState.Disabled, null))),
            state.ReadSettings()!.Settings.Select(entry => (entry.Key, entry.Value)));
    }

    [Fact]
    public async Task A_record_that_cannot_be_written_is_logged_and_written_at_the_next_refresh_while_the_values_apply()
    {
        var ct = TestContext.Current.CancellationToken;
        github.Set(Setting, "4");
        state.FailWrites = true;
        var settings = Lifetime();

        await settings.Refresh(ct);
        Assert.Equal("4", settings[Setting].Value);
        Assert.Null(state.ReadSettings());
        Assert.Contains(("repository-settings-error", "the observed settings could not be recorded: No space left on device"), log.Entries);

        // Nothing changed, but nothing is recorded yet.
        state.FailWrites = false;
        Assert.False(await settings.Refresh(ct));
        Assert.Equal(new SettingValue(SettingState.Configured, "4") { Since = Start }, state.ReadSettings()!.Settings[Setting]);

        // Once recorded, an unchanged refresh writes nothing.
        Assert.False(await settings.Refresh(ct));
        Assert.Equal(1, state.SettingsWrites);
    }

    [Fact]
    public void Each_state_has_the_name_that_logs_status_and_the_record_use()
    {
        Assert.Equal(["pending", "disabled", "absent", "configured", "invalid", "read-failed"], Enum.GetValues<SettingState>().Select(state => state.Name()));
        Assert.All(Enum.GetValues<SettingState>(), state => Assert.Equal(state, SettingStates.Parse(state.Name())));
        Assert.Null(SettingStates.Parse("read_failed"));
        Assert.Null(SettingStates.Parse(null));
    }
}
