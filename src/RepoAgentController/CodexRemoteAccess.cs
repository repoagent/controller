using System.Text.Json.Nodes;

namespace RepoAgentController;

sealed partial class Codex : IAgentRemoteAccess
{
    public int CurrentGeneration => Volatile.Read(ref epoch);
    public event Action<AgentRemoteAccessObservation>? RemoteAccessChanged;
    public event Func<CancellationToken, Task>? RemoteAccessReady;

    void ObserveRemote(int source, string method, JsonNode? parameters)
    {
        if (source != CurrentGeneration) return;
        if (method == "remoteControl/status/changed") PublishRemote(ParseRemote(source, parameters));
        else if (method == "disconnect") RemoteAccessChanged?.Invoke(new(source, AgentRemoteConnection.Unavailable));
    }

    void PublishRemote(AgentRemoteAccessObservation observation)
    {
        if (observation.Generation != CurrentGeneration) return;
        RemoteAccessChanged?.Invoke(observation);
        if (observation.Connection is AgentRemoteConnection.Connecting or AgentRemoteConnection.Connected)
            Changed?.Invoke(new AgentNativeControlChanged(observation.Generation, true));
        else if (observation.Connection == AgentRemoteConnection.Disabled)
            Changed?.Invoke(new AgentNativeControlChanged(observation.Generation, false));
    }

    static AgentRemoteAccessObservation ParseRemote(int generation, JsonNode? value) => new(generation,
        value?["status"].OptionalText() switch
        {
            "disabled" => AgentRemoteConnection.Disabled,
            "connecting" => AgentRemoteConnection.Connecting,
            "connected" => AgentRemoteConnection.Connected,
            "errored" => AgentRemoteConnection.Error,
            _ => AgentRemoteConnection.Unavailable
        }, value?["environmentId"].OptionalText());

    public Task<AgentRemoteAccessObservation> ReadRemoteAccess(CancellationToken ct) => RemoteStatus("remoteControl/status/read", ct);
    public Task<AgentRemoteAccessObservation> EnableRemoteAccess(CancellationToken ct)
    {
        // Observe trusted owner work even while the provider connects. A timeout may mean it did enable.
        Changed?.Invoke(new AgentNativeControlChanged(CurrentGeneration, true));
        return RemoteStatus("remoteControl/enable", ct);
    }
    public Task<AgentRemoteAccessObservation> DisableRemoteAccess(CancellationToken ct) => RemoteStatus("remoteControl/disable", ct);

    async Task<AgentRemoteAccessObservation> RemoteStatus(string method, CancellationToken ct)
    {
        var source = CurrentGeneration;
        var response = await Request(method, method == "remoteControl/status/read" ? new JsonObject() : Json.Object(("ephemeral", true)), ct);
        var observation = ParseRemote(source, response);
        PublishRemote(observation);
        return observation;
    }

    public async Task<AgentRemotePair> PairRemoteAccess(CancellationToken ct)
    {
        var response = await Request("remoteControl/pairing/start", Json.Object(("manualCode", true)), ct);
        var code = response["manualPairingCode"].OptionalText();
        if (string.IsNullOrWhiteSpace(code)) throw new IOException("Runtime returned no manual pairing code.");
        Log.Protect(code);
        if (response["pairingCode"].OptionalText() is { Length: > 0 } pairingCode) Log.Protect(pairingCode);
        return new(code, Timestamp(response["expiresAt"]) ?? throw new IOException("Runtime returned no pairing expiry."));
    }

    public async Task<AgentRemoteClients> ListRemoteClients(string environmentId, string? cursor, CancellationToken ct)
    {
        var response = await Request("remoteControl/client/list", Json.Object(("environmentId", environmentId),
            ("cursor", cursor), ("limit", 100), ("order", "asc")), ct);
        if (response["data"] is not JsonArray data || data.Count > 100) throw new IOException("Runtime client list is invalid.");
        var clients = new List<AgentRemoteClient>();
        foreach (var client in data)
        {
            var id = client?["clientId"].OptionalText();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512) throw new IOException("Runtime client ID is invalid.");
            clients.Add(new(id, Bounded(client?["displayName"]), Bounded(client?["deviceType"]),
                Bounded(client?["platform"]), Timestamp(client?["lastSeenAt"])));
        }
        var next = response["nextCursor"].OptionalText();
        if (next?.Length > 8192) throw new IOException("Runtime client cursor exceeds its bound.");
        return new(clients, next);
    }

    public async Task RevokeRemoteClient(string environmentId, string clientId, CancellationToken ct) =>
        _ = await Request("remoteControl/client/revoke", Json.Object(("environmentId", environmentId), ("clientId", clientId)), ct);

    static string? Bounded(JsonNode? value) => value.OptionalText() is { } text ? text[..Math.Min(text.Length, 128)] : null;
    static DateTimeOffset? Timestamp(JsonNode? value)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<long>(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw new IOException("Runtime timestamp is invalid."); }
    }
}
