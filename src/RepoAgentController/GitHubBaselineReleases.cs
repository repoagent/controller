using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>The <see cref="IBaselineReleases"/> adapter: the newest page of repoagent/controller's public GitHub releases, read
/// through gh. A release whose tag, state or metadata does not describe a supported baseline release is left out.</summary>
sealed class GitHubBaselineReleases(GitHub github) : IBaselineReleases
{
    const string Marker = "<!-- repoagent.baseline-release/v1 -->", SemanticMarker = "<!-- repoagent.controller-release/v2 -->";

    public async Task<IReadOnlyList<BaselineRelease>> Published(CancellationToken ct)
    {
        var (status, body) = await github.Get($"repos/{BaselineReleaseCheck.Repository}/releases?per_page=100", "releases", ct);
        if (status != 200) throw Rejected(status);
        JsonArray releases;
        try { releases = JsonNode.Parse(body) as JsonArray ?? throw new IOException("Malformed GitHub releases response"); }
        catch (JsonException) { throw new IOException("Malformed GitHub releases response"); }
        return releases.OfType<JsonObject>().Select(Parse).OfType<BaselineRelease>().ToArray();
    }

    /// <summary>The releases read answered with an unexpected HTTP status.</summary>
    static GitHubRequestException Rejected(int status) => new(status switch
    {
        429 => GitHubFailure.RateLimited,
        400 or 403 or 404 or 410 or 422 => GitHubFailure.Permanent,
        _ => GitHubFailure.Transient
    }, $"GitHub returned HTTP {status}");

    /// <summary>A supported release: a published baseline tag whose v1 metadata describes the public linux/amd64 image.</summary>
    static BaselineRelease? Parse(JsonObject release)
    {
        var tag = Regex.Match(Field(release, "tag_name"), @"\Abaseline-([0-9]{1,18})\.([0-9]{1,9})\z");
        var name = Field(release, "tag_name");
        var semantic = Regex.IsMatch(name, @"\Acontroller-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") &&
            Version.TryParse(name[12..], out _);
        if ((!tag.Success && !semantic) || !IsFalse(release, "draft") || !IsFalse(release, "prerelease")) return null;
        var block = Regex.Match(Field(release, "body"), "^" + Regex.Escape(semantic ? SemanticMarker : Marker) + @"\r?\n```json\r?\n(.*?)\r?\n```", RegexOptions.Multiline | RegexOptions.Singleline);
        JsonObject? metadata;
        JsonNode? versions;
        try
        {
            metadata = block.Success ? JsonNode.Parse(block.Groups[1].Value) as JsonObject : null;
            versions = metadata?["versions"];
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return null; } // Including duplicate keys.
        if (Field(metadata, "schema") != (semantic ? "repoagent.controller-release/v2" : "repoagent.baseline-release/v1") || Field(metadata, "release") != name || Field(metadata, "platform") != "linux/amd64" ||
            !Regex.IsMatch(Field(metadata, "image"), @"\Aghcr\.io/repoagent/controller@sha256:[0-9a-f]{64}\z") ||
            !Regex.IsMatch(Field(metadata, "inventorySha256"), @"\A[0-9a-f]{64}\z") || !Regex.IsMatch(Field(versions, "controller"), @"\A[0-9a-f]{40}\z") ||
            !Regex.IsMatch(Field(versions, "gh"), @"\A[0-9A-Za-z.+-]{1,64}\z") || !Regex.IsMatch(Field(versions, "codex"), @"\A[0-9A-Za-z.+-]{1,64}\z"))
            return null;
        if (semantic && (Field(metadata, "version") != name[12..] || Field(metadata, "source") != Field(versions, "controller") ||
                !Regex.IsMatch(Field(metadata, "notesSha256"), @"\A[0-9a-f]{64}\z"))) return null;
        return new(name, semantic ? 0 : long.Parse(tag.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), semantic ? 0 : int.Parse(tag.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            Field(metadata, "image"), Field(metadata, "inventorySha256"), Field(versions, "controller"), Field(versions, "gh"), Field(versions, "codex"));
    }

    // Release data is external input: a field of another type or shape reads as absent instead of failing the check.
    static string Field(JsonNode? node, string name) => (node as JsonObject)?[name].OptionalText() ?? "";

    static bool IsFalse(JsonObject node, string name) => node[name] is JsonValue field && field.TryGetValue<bool>(out var value) && !value;
}
