using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller state file in a private temporary directory.
public sealed class StateStoreTests : IDisposable
{
    const string Repository = "owner/repository";
    readonly string directory = Path.Combine(Path.GetTempPath(), "state-store-" + Guid.NewGuid().ToString("N"));
    string State => Path.Combine(directory, "bridge.json");

    [Fact]
    public void Discussions_whose_removal_failed_to_save_stay_in_memory_and_a_retry_removes_them_from_the_file()
    {
        using (var store = new StateStore(State, Repository))
        {
            store.Discussions["D1"] = new JsonObject { ["url"] = "https://github.com/owner/repository/discussions/1", ["sessionId"] = "S1" };
            store.Discussions["D2"] = new JsonObject { ["url"] = "https://github.com/owner/repository/discussions/2", ["sessionId"] = "S2" };
            store.Save();
            Directory.CreateDirectory(State + ".tmp"); // The next save cannot write its replacement file.

            var error = Record.Exception(() => store.Remove(["D1"]));

            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a failed save, not {error}");
            Assert.Equal(["D1", "D2"], store.Discussions.Select(entry => entry.Key).Order());
            Directory.Delete(State + ".tmp");
            store.Remove(["D1"]);
        }
        using var restarted = new StateStore(State, Repository);
        Assert.Equal(["D2"], restarted.Discussions.Select(entry => entry.Key));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
