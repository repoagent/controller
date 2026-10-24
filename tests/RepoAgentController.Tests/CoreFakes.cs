using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>The repository's REPOAGENT_ Actions variables as GitHub holds them. A queued failure answers a read instead.</summary>
sealed class InMemoryRepositorySettings : IRepositorySettings
{
    readonly Lock sync = new();
    readonly Dictionary<string, string> variables = [];
    readonly Dictionary<string, Exception> failures = [];
    readonly List<string> reads = [];

    /// <summary>Sets a variable, or removes it when <paramref name="value"/> is null, and clears any failure of its reads.</summary>
    public void Set(string name, string? value)
    {
        lock (sync)
        {
            failures.Remove(name);
            if (value is null) variables.Remove(name);
            else variables[name] = value;
        }
    }

    /// <summary>Later reads of <paramref name="name"/> fail with <paramref name="failure"/> until it is set again.</summary>
    public void Fail(string name, Exception failure) { lock (sync) failures[name] = failure; }

    /// <summary>Every read so far, in order, by variable name.</summary>
    public IReadOnlyList<string> Reads { get { lock (sync) return [.. reads]; } }

    public Task<string?> Read(string name, CancellationToken ct)
    {
        lock (sync)
        {
            reads.Add(name);
            return failures.TryGetValue(name, out var failure) ? Task.FromException<string?>(failure) : Task.FromResult(variables.GetValueOrDefault(name));
        }
    }
}

/// <summary>The controller state as its files hold it: only what was written survives into another lifetime. A write can fail
/// on demand, as a full or read-only disk does.</summary>
sealed class InMemoryControllerState : IControllerState
{
    RecordedSettings? settings;

    /// <summary>Later writes throw <see cref="IOException"/>, leaving the earlier record in place.</summary>
    public bool FailWrites { get; set; }

    public int SettingsWrites { get; private set; }

    public RecordedSettings? ReadSettings() => settings;

    public void WriteSettings(RecordedSettings recorded)
    {
        if (FailWrites) throw new IOException("No space left on device");
        SettingsWrites++;
        settings = new(new Dictionary<string, SettingValue>(recorded.Settings));
    }
}

/// <summary>The controller's log, as kind and message pairs in the order the core wrote them.</summary>
sealed class RecordingLog : IControllerLog
{
    readonly Lock sync = new();
    readonly List<(string Kind, string Message)> entries = [];

    public IReadOnlyList<(string Kind, string Message)> Entries { get { lock (sync) return [.. entries]; } }

    public void Write(string kind, string message) { lock (sync) entries.Add((kind, message)); }
}

/// <summary>Controlled time that reports each timer a loop arms, so a test moves time only once the loop waits.</summary>
sealed class SteppedClock(DateTimeOffset start) : TimeProvider
{
    readonly FakeTimeProvider time = new(start);
    readonly SemaphoreSlim armed = new(0);

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
    public override long GetTimestamp() => time.GetTimestamp();
    public override long TimestampFrequency => time.TimestampFrequency;
    public void Advance(TimeSpan duration) => time.Advance(duration);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = time.CreateTimer(callback, state, dueTime, period);
        armed.Release();
        return timer;
    }

    /// <summary>Completes once the loop has armed its next timer, that is, once it waits again.</summary>
    public Task Armed() => armed.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>Whether a timer was armed since the last <see cref="Armed"/>, without waiting.</summary>
    public bool WasArmed() => armed.CurrentCount != 0;
}
