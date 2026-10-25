using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// Operator-supplied Codex account credentials over a private native home in a temporary directory: what each start installs
// there, and what an unusable supply reveals. Whether the pinned Codex then signs in with its home's auth.json is not tested.
public sealed class CodexCredentialsTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "codex-credentials-" + Guid.NewGuid().ToString("N"));
    readonly string home, supplied;

    public CodexCredentialsTests()
    {
        home = Path.Combine(directory, "native-home");
        Directory.CreateDirectory(home);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        supplied = Path.Combine(directory, "auth.json");
    }

    static string Credential(string apiKey) => new JsonObject { ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = apiKey }.ToJsonString();

    string Installed() => JsonNode.Parse(File.ReadAllText(Path.Combine(home, "auth.json")))!["OPENAI_API_KEY"]!.GetValue<string>();

    [Fact]
    public void Each_start_keeps_the_natives_refreshed_credentials_until_the_operator_supplies_new_ones()
    {
        File.WriteAllText(supplied, Credential("sk-canary-A"));
        CodexCredentials.Open(supplied, home).Install();
        Assert.Equal("sk-canary-A", Installed());

        // The native refreshes its own sign-in, as Codex rotates ChatGPT tokens into its home. An unchanged supply must not
        // overwrite that at the next start, or every restart would reinstate an already-rotated refresh token.
        File.WriteAllText(Path.Combine(home, "auth.json"), Credential("sk-canary-rotated"));
        CodexCredentials.Open(supplied, home).Install();
        Assert.Equal("sk-canary-rotated", Installed());

        File.WriteAllText(supplied, Credential("sk-canary-B"));
        CodexCredentials.Open(supplied, home).Install();
        Assert.Equal("sk-canary-B", Installed());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-json")]
    [InlineData("no-credential")]
    public void An_unusable_supply_is_refused_naming_the_file_but_not_its_content_and_installs_nothing(string supply)
    {
        const string Canary = "sk-canary-unusable";
        if (supply == "not-json") File.WriteAllText(supplied, "{\"OPENAI_API_KEY\":\"" + Canary);
        if (supply == "no-credential") File.WriteAllText(supplied, new JsonObject { ["auth_mode"] = "apikey", ["note"] = Canary }.ToJsonString());

        var error = Assert.Throws<ArgumentException>(() => CodexCredentials.Open(supplied, home));

        Assert.StartsWith("--codex-auth file " + supplied + " ", error.Message);
        Assert.DoesNotContain(Canary, error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(home));
    }

    // The entrypoint checks agent account credentials before, and independently of, any GitHub App credential use.
    [Fact]
    public async Task An_unusable_supply_stops_startup_before_a_GitHub_App_credential_is_acquired()
    {
        using var fixture = new BrokerAuthenticationTests.Fixture();
        string[] arguments = ["--repo", "owner/repository", "--app-id", "123", "--state", fixture.State, "--broker-url", "https://credentials.example.org",
            "--enrollment-file", fixture.EnrollmentFile, "--workspace", directory, "--codex-auth", supplied, "--once"];

        Assert.Equal(1, await BridgeCommand.Run(arguments, fixture.Http, fixture.Gh, fixture.Clock, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Transport.Requests);
        var status = await OperatorStatusTests.Status(fixture.State);
        Assert.Equal("failed", status["controller"]!["state"]!.GetValue<string>());
        Assert.Contains(status["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "fatal" && item["message"]!.GetValue<string>().Contains("--codex-auth"));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
