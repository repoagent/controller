using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IWebhookRegistration"/> adapter: a repository webhook registered through gh's REST API with the
/// installation token, whose ID, URL and secret are saved in <c>hook.json</c> beside the controller's state file.</summary>
sealed class GitHubWebhookRegistration : IWebhookRegistration
{
    readonly GitHub github;
    readonly string path;
    readonly JsonObject settings;
    public string Secret => settings.Text("secret");

    public GitHubWebhookRegistration(GitHub github, string statePath)
    {
        this.github = github;
        var directory = statePath + ".webhook";
        GitHubAppCredentials.SecureDirectory(directory);
        path = Path.Combine(directory, "hook.json");
        settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() :
            Json.Object(("repository", github.Repository), ("secret", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
        if (!string.Equals(settings.Text("repository"), github.Repository, StringComparison.OrdinalIgnoreCase) || Secret.Length != 64)
            throw new InvalidOperationException("Stored repository webhook configuration does not match this repository");
        Log.Protect(Secret);
        Save();
    }

    public async Task Configure(Uri endpoint, bool push, CancellationToken ct)
    {
        var hook = settings["hookId"]?.GetValue<long>();
        var input = Json.Object(("name", "web"), ("active", true),
            ("events", push ? new JsonArray("discussion", "discussion_comment", "push") : new JsonArray("discussion", "discussion_comment")),
            ("config", Json.Object(("url", endpoint.AbsoluteUri), ("content_type", "json"), ("insecure_ssl", "0"), ("secret", Secret))));
        JsonNode? result;
        try { result = await github.Rest(hook is null ? "POST" : "PATCH", hook is null ? "hooks" : $"hooks/{hook}", input, ct); }
        catch (IOException error) { throw new IOException("Repository webhook registration failed; check the saved hook and approved Webhooks read/write permission. " + error.Message, error); }
        var id = result?["id"]?.GetValue<long>() ?? 0;
        if (id <= 0 || (hook is not null && id != hook)) throw new IOException("GitHub returned an unexpected repository webhook ID");
        settings["hookId"] = id;
        settings["url"] = endpoint.AbsoluteUri;
        Save();
        Log.Write(hook is null ? "webhook-registered" : "webhook-updated", $"hook={id} url={endpoint}");
    }

    public async Task<bool> Deactivate()
    {
        if (settings["hookId"]?.GetValue<long>() is not { } hook) return true;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Omit config entirely: preserve the secret, URL and subscribed events.
            var result = await github.Rest("PATCH", $"hooks/{hook}", Json.Object(("active", false)), cleanup.Token);
            if (result?["id"]?.GetValue<long>() != hook || result?["active"]?.GetValue<bool>() != false)
                throw new IOException("GitHub did not confirm that the saved hook is inactive");
            Log.Write("webhook-disabled", $"hook={hook}");
            return true;
        }
        catch (Exception error)
        {
            Log.Write("webhook-disable-error", $"hook={hook}; disable not confirmed; manually deactivate the saved hook in repository Settings > Webhooks. {error.Message}");
            return false;
        }
    }

    void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, settings.ToJsonString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, true);
    }
}
