using System.Text.Json.Nodes;

namespace RepoAgentController;

sealed class StateStore : IDisposable
{
    readonly string path;
    readonly FileStream lease;
    public JsonObject State { get; }
    public JsonObject Discussions => State["discussions"]!.AsObject();

    public StateStore(string path, string repository, Action? onLockAcquired = null)
    {
        this.path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lease = ExclusiveFileLease.Open(path + ".lock");
        try
        {
            onLockAcquired?.Invoke();
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

    /// <summary>Removes these Discussion records and saves. When the save fails they are restored before the error propagates,
    /// so memory still matches the state file and a retry removes and saves them again.</summary>
    public void Remove(IReadOnlyCollection<string> ids)
    {
        var removed = ids.Where(Discussions.ContainsKey).Select(id => (Id: id, Record: Discussions[id])).ToArray();
        foreach (var (id, _) in removed) Discussions.Remove(id);
        try { Save(); }
        catch
        {
            foreach (var (id, record) in removed) Discussions[id] = record;
            throw;
        }
    }

    public void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, State.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public void Dispose() => lease.Dispose();
}
