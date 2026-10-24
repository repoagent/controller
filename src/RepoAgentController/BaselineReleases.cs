using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>One published baseline release, as its machine-readable release metadata describes it.</summary>
sealed record BaselineRelease(string Tag, long Run, int Attempt, string Image, string InventorySha256, string Commit, string Gh, string Codex)
{
    public string Url => $"https://github.com/{BaselineReleases.Repository}/releases/tag/{Tag}";
}

/// <summary>
/// One release check: this controller's release, if its image identifies one, and the newest published release.
/// An uncertain identity names its reason instead of being offered a replacement. A failed check knows neither
/// release, so it never claims that the controller is current or that an update exists.
/// </summary>
sealed record ReleaseNotice(DateTimeOffset CheckedAt, BaselineRelease? Running, BaselineRelease? Newest, int Newer, string? Uncertainty, string? Failure = null);

/// <summary>
/// Compares this controller's baseline image with the baseline releases published by repoagent/controller, about hourly.
/// It only reads: adopting a newer release is an operator's deliberate recreation of the controller, never a self-update.
/// </summary>
sealed class BaselineReleases(GitHub github, string? inventory, bool startupToolUpdates, TimeProvider clock) : IAsyncDisposable
{
    public const string Repository = "repoagent/controller";
    const string Marker = "<!-- repoagent.baseline-release/v1 -->";
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    readonly CancellationTokenSource stop = new();
    Task running = Task.CompletedTask;
    volatile ReleaseNotice? current;

    /// <summary>The latest completed check, or null before the first one completes.</summary>
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
                ReleaseNotice notice;
                try { notice = await Check(ct); }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    notice = new(clock.GetUtcNow(), null, null, 0, null, Log.Redact(error.Message));
                }
                // Logged when the outcome changes, not on every hourly check.
                if (current is not { } previous || previous with { CheckedAt = default } != notice with { CheckedAt = default })
                    Log.Write(notice.Failure is null ? "release-check" : "release-check-error", notice.Failure is { } failure ? failure + "; the next check is in about an hour"
                        : $"running={notice.Running?.Tag ?? "unidentified"} newest={notice.Newest?.Tag ?? "none"} newer={notice.Newer} identity={(notice.Uncertainty is null ? "known" : "uncertain")}");
                current = notice;
                Changed?.Invoke();
                await Task.Delay(CheckInterval, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    async Task<ReleaseNotice> Check(CancellationToken ct)
    {
        var identity = inventory is null ? null : Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(inventory, ct)));
        // Newest first by publication run and attempt, which the tag records; GitHub's Latest badge is not used.
        var releases = (await github.Releases(Repository, ct)).OfType<JsonObject>().Select(Parse).OfType<BaselineRelease>()
            .OrderByDescending(release => release.Run).ThenByDescending(release => release.Attempt).ToArray();
        var matched = Array.FindIndex(releases, release => release.InventorySha256 == identity);
        var uncertainty = identity is null ? "this controller was not started with a baseline image inventory, for example in a native or development run"
            : matched < 0 ? "its image inventory matches none of the newest published baseline releases, so it is a derived, local, unreleased or much older image"
            : startupToolUpdates ? "startup tool updates replace the image's gh and Codex, possibly with versions that no release records" : null;
        return new(clock.GetUtcNow(), matched < 0 ? null : releases[matched], releases.FirstOrDefault(), Math.Max(matched, 0), uncertainty);
    }

    /// <summary>A supported release: a published baseline tag whose v1 metadata describes the public linux/amd64 image.</summary>
    static BaselineRelease? Parse(JsonObject release)
    {
        var tag = Regex.Match(Field(release, "tag_name"), @"\Abaseline-([0-9]{1,18})\.([0-9]{1,9})\z");
        if (!tag.Success || !IsFalse(release, "draft") || !IsFalse(release, "prerelease")) return null;
        var block = Regex.Match(Field(release, "body"), "^" + Regex.Escape(Marker) + @"\r?\n```json\r?\n(.*?)\r?\n```", RegexOptions.Multiline | RegexOptions.Singleline);
        JsonObject? metadata = null;
        JsonNode? versions = null;
        try
        {
            metadata = block.Success ? JsonNode.Parse(block.Groups[1].Value) as JsonObject : null;
            versions = metadata?["versions"];
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; } // Including duplicate keys.
        if (Field(metadata, "schema") != "repoagent.baseline-release/v1" || Field(metadata, "release") != tag.Value || Field(metadata, "platform") != "linux/amd64" ||
            !Regex.IsMatch(Field(metadata, "image"), @"\Aghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}\z") ||
            !Regex.IsMatch(Field(metadata, "inventorySha256"), @"\A[0-9a-f]{64}\z") || !Regex.IsMatch(Field(versions, "controller"), @"\A[0-9a-f]{40}\z") ||
            !Regex.IsMatch(Field(versions, "gh"), @"\A[0-9A-Za-z.+-]{1,64}\z") || !Regex.IsMatch(Field(versions, "codex"), @"\A[0-9A-Za-z.+-]{1,64}\z"))
            return null;
        return new(tag.Value, long.Parse(tag.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(tag.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            Field(metadata, "image"), Field(metadata, "inventorySha256"), Field(versions, "controller"), Field(versions, "gh"), Field(versions, "codex"));
    }

    // Release data is external input: a field of another type or shape reads as absent instead of failing the check.
    static string Field(JsonNode? node, string name) => (node as JsonObject)?[name].OptionalText() ?? "";

    static bool IsFalse(JsonObject node, string name) => node[name] is JsonValue field && field.TryGetValue<bool>(out var value) && !value;

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await running;
        stop.Dispose();
    }
}
