using System.Text.Json.Nodes;

namespace DiscussionBridge;

sealed class StateStore : IDisposable
{
    readonly string path;
    readonly FileStream lease;
    public JsonObject State { get; }
    public JsonObject Discussions => State["discussions"]!.AsObject();

    public StateStore(string path, string repository)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            State = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject
            {
                ["version"] = 1, ["repository"] = repository, ["discussions"] = new JsonObject()
            };
            if (State["version"]?.GetValue<int>() != 1 || !string.Equals(State.Text("repository"), repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("State belongs to a different repository or unsupported version");
        }
        catch { lease.Dispose(); throw; }
    }

    public void VerifyIdentity(string login)
    {
        if (State["agentLogin"] is not null && !string.Equals(State.Text("agentLogin"), login, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("State belongs to a different GitHub identity; use a fresh state file when moving from a user login to a GitHub App");
        State["agentLogin"] = login;
        Save();
    }

    public void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, State.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public void Dispose() => lease.Dispose();
}
