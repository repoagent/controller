namespace RepoAgentController;

/// <summary>One published baseline release, as its machine-readable release metadata describes it.</summary>
public sealed record BaselineRelease(string Tag, long Run, int Attempt, string Image, string InventorySha256, string Commit, string Gh, string Codex)
{
    public string Url => $"https://github.com/{BaselineReleases.Repository}/releases/tag/{Tag}";
}

/// <summary>
/// One release check: this controller's release, if its image identifies one, and the newest published release.
/// An uncertain identity names its reason instead of being offered a replacement. A failed check knows neither
/// release, so it never claims that the controller is current or that an update exists.
/// </summary>
public sealed record ReleaseNotice(DateTimeOffset CheckedAt, BaselineRelease? Running, BaselineRelease? Newest, int Newer, string? Uncertainty, string? Failure = null);

/// <summary>
/// Compares this controller's baseline image with the baseline releases published by repoagent/controller, about hourly.
/// It only reads: adopting a newer release is an operator's deliberate recreation of the controller, never a self-update.
/// </summary>
/// <param name="inventorySha256">The SHA-256 of this controller's image inventory, read at every check; null when the
/// controller was started without one.</param>
public sealed class BaselineReleases(IBaselineReleases published, Func<CancellationToken, Task<string?>> inventorySha256, bool startupToolUpdates,
    TimeProvider clock, IControllerLog log) : IAsyncDisposable
{
    public const string Repository = "repoagent/controller";
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    readonly CancellationTokenSource stop = new();
    Task running = Task.CompletedTask;
    volatile ReleaseNotice? current;

    /// <summary>The latest completed check, or null before the first one completes. A failure is not redacted; redact it
    /// before it leaves the controller.</summary>
    public ReleaseNotice? Current => current;

    /// <summary>Raised after each completed check. Handlers must not log.</summary>
    public event Action? Changed;

    public void Start(CancellationToken ct) => running = Run(CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token));

    async Task Run(CancellationTokenSource lifetime)
    {
        var ct = lifetime.Token;
        try
        {
            while (true)
            {
                await Check(ct);
                await Task.Delay(CheckInterval, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    /// <summary>Checks once: <see cref="Current"/> becomes the outcome, which is logged when it differs from the previous one.</summary>
    public async Task Check(CancellationToken ct)
    {
        ReleaseNotice notice;
        try { notice = await Compare(ct); }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            notice = new(clock.GetUtcNow(), null, null, 0, null, error.Message);
        }
        // Logged when the outcome changes, not on every hourly check.
        if (current is not { } previous || previous with { CheckedAt = default } != notice with { CheckedAt = default })
            log.Write(notice.Failure is null ? "release-check" : "release-check-error", notice.Failure is { } failure ? failure + "; the next check is in about an hour"
                : $"running={notice.Running?.Tag ?? "unidentified"} newest={notice.Newest?.Tag ?? "none"} newer={notice.Newer} identity={(notice.Uncertainty is null ? "known" : "uncertain")}");
        current = notice;
        Changed?.Invoke();
    }

    async Task<ReleaseNotice> Compare(CancellationToken ct)
    {
        var identity = await inventorySha256(ct);
        // Newest first by publication run and attempt, which the tag records; GitHub's Latest badge is not used.
        var releases = (await published.Published(ct)).OrderByDescending(release => release.Run).ThenByDescending(release => release.Attempt).ToArray();
        var matched = Array.FindIndex(releases, release => release.InventorySha256 == identity);
        var uncertainty = identity is null ? "this controller was not started with a baseline image inventory, for example in a native or development run"
            : matched < 0 ? "its image inventory matches none of the newest published baseline releases, so it is a derived, local, unreleased or much older image"
            : startupToolUpdates ? "startup tool updates replace the image's gh and Codex, possibly with versions that no release records" : null;
        return new(clock.GetUtcNow(), matched < 0 ? null : releases[matched], releases.FirstOrDefault(), Math.Max(matched, 0), uncertainty);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await running;
        stop.Dispose();
    }
}
