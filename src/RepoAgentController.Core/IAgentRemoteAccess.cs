namespace RepoAgentController;

public enum AgentRemoteConnection { Disabled, Connecting, Connected, Unavailable, Error }
public sealed record AgentRemoteAccessObservation(int Generation, AgentRemoteConnection Connection, string? EnvironmentId = null);
public sealed record AgentRemotePair(string Code, DateTimeOffset ExpiresAt);
public sealed record AgentRemoteClient(string Id, string? Name, string? Device, string? Platform, DateTimeOffset? LastSeenAt);
public sealed record AgentRemoteClients(IReadOnlyList<AgentRemoteClient> Clients, string? NextCursor);

/// <summary>Native owner access on the same owned runtime. Provider JSON and account authentication stay in its adapter.</summary>
public interface IAgentRemoteAccess
{
    int CurrentGeneration { get; }
    event Action<AgentRemoteAccessObservation>? RemoteAccessChanged;
    event Func<CancellationToken, Task>? RemoteAccessReady;
    Task<AgentRemoteAccessObservation> ReadRemoteAccess(CancellationToken ct);
    Task<AgentRemoteAccessObservation> EnableRemoteAccess(CancellationToken ct);
    Task<AgentRemoteAccessObservation> DisableRemoteAccess(CancellationToken ct);
    Task<AgentRemotePair> PairRemoteAccess(CancellationToken ct);
    Task<AgentRemoteClients> ListRemoteClients(string environmentId, string? cursor, CancellationToken ct);
    Task RevokeRemoteClient(string environmentId, string clientId, CancellationToken ct);
}
