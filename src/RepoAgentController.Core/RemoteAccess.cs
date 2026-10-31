namespace RepoAgentController;

// Supplied only by a trusted driving adapter after authenticating the local caller.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Trusted local operation permissions.")]
public enum LocalCallerPermission { Unknown, Inspection, Administration }
public enum RemoteOperation { Unknown, Enable, Pair, Status, Clients, Revoke, Disable }
public enum RemoteAccessOutcome { Success, Forbidden, Unavailable, Rejected, Uncertain }
public sealed record RemoteAccessStatus(bool DesiredEnabled, AgentRemoteConnection Connection, int Generation, bool Unconfirmed);
public sealed record RemoteAccessResult(RemoteAccessOutcome Outcome, string Message, RemoteAccessStatus? Status = null,
    AgentRemotePair? Pair = null, IReadOnlyList<AgentRemoteClient>? Clients = null);

/// <summary>Owner intent and bounded native-access operations, independent of Discussion availability.</summary>
public sealed class RemoteAccess : IDisposable
{
    readonly IAgentRemoteAccess runtime;
    readonly IControllerState state;
    readonly TimeProvider clock;
    readonly SemaphoreSlim gate = new(1);
    AgentRemoteAccessObservation? observed;
    bool desired;

    public RemoteAccess(IAgentRemoteAccess runtime, IControllerState state, TimeProvider clock)
    {
        this.runtime = runtime; this.state = state; this.clock = clock;
        desired = state.ReadRemoteAccessEnabled();
        runtime.RemoteAccessChanged += Observe;
        runtime.RemoteAccessReady += Restore;
    }

    void Observe(AgentRemoteAccessObservation observation)
    {
        if (observation.Generation == runtime.CurrentGeneration) Volatile.Write(ref observed, observation);
    }

    RemoteAccessStatus Status(bool unconfirmed = false)
    {
        var current = Volatile.Read(ref observed);
        var matches = current?.Generation == runtime.CurrentGeneration;
        return new(desired, matches ? current!.Connection : AgentRemoteConnection.Unavailable,
            runtime.CurrentGeneration, unconfirmed || !matches || current!.Connection is AgentRemoteConnection.Unavailable or AgentRemoteConnection.Error);
    }

    public async Task Restore(CancellationToken ct)
    {
        _ = await Run(desired ? RemoteOperation.Enable : RemoteOperation.Disable, null, persist: false, ct);
    }

    public Task<RemoteAccessResult> Execute(RemoteOperation operation, LocalCallerPermission permission, string? clientId, CancellationToken ct)
    {
        if (permission != LocalCallerPermission.Administration)
            return Task.FromResult(new RemoteAccessResult(RemoteAccessOutcome.Forbidden, "Remote administration permission is required."));
        if (operation is < RemoteOperation.Enable or > RemoteOperation.Disable)
            return Task.FromResult(new RemoteAccessResult(RemoteAccessOutcome.Forbidden, "Remote operation is not authorized."));
        if (operation == RemoteOperation.Revoke && (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 512))
            return Task.FromResult(new RemoteAccessResult(RemoteAccessOutcome.Rejected, "Choose an explicit client ID."));
        return Run(operation, clientId, persist: true, ct);
    }

    async Task<RemoteAccessResult> Run(RemoteOperation operation, string? clientId, bool persist, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var entered = false;
        var issued = false;
        var generation = runtime.CurrentGeneration;
        try
        {
            await gate.WaitAsync(bounded.Token); entered = true;
            generation = runtime.CurrentGeneration;
            if (!persist) operation = desired ? RemoteOperation.Enable : RemoteOperation.Disable;
            if (operation is RemoteOperation.Enable or RemoteOperation.Disable)
            {
                var enabled = operation == RemoteOperation.Enable;
                // Disable intent is durable before touching the runtime. Pairing and client records are never retained here.
                if (persist) state.WriteRemoteAccessEnabled(enabled);
                desired = enabled;
                issued = true;
                var result = enabled ? await runtime.EnableRemoteAccess(bounded.Token) : await runtime.DisableRemoteAccess(bounded.Token);
                CheckGeneration(generation, result.Generation); Observe(result);
                var confirmed = enabled ? result.Connection is AgentRemoteConnection.Connecting or AgentRemoteConnection.Connected
                    : result.Connection == AgentRemoteConnection.Disabled;
                return new(confirmed ? RemoteAccessOutcome.Success : RemoteAccessOutcome.Uncertain,
                    confirmed ? enabled ? "Native access enabled; pairing requires connected status." : "Native access disabled; remembered clients were not revoked."
                        : "Intent recorded; current native access is unconfirmed.", Status(!confirmed));
            }
            var observation = await runtime.ReadRemoteAccess(bounded.Token);
            CheckGeneration(generation, observation.Generation); Observe(observation);
            if (operation == RemoteOperation.Status)
                return new(RemoteAccessOutcome.Success, "Current native access observation.", Status());
            if (!desired || observation.Connection != AgentRemoteConnection.Connected || observation.EnvironmentId is null)
                return new(RemoteAccessOutcome.Rejected, "Native access must be enabled and connected first.", Status());
            if (operation == RemoteOperation.Pair)
            {
                issued = true;
                var pair = await runtime.PairRemoteAccess(bounded.Token);
                CheckGeneration(generation, runtime.CurrentGeneration);
                if (string.IsNullOrWhiteSpace(pair.Code) || pair.Code.Length > 1024 || pair.ExpiresAt <= clock.GetUtcNow())
                    return new(RemoteAccessOutcome.Uncertain, "Pairing did not return a usable unexpired code; request a new code explicitly.", Status(true));
                return new(RemoteAccessOutcome.Success, "Private manual pairing code; use before its expiry.", Status(), Pair: pair);
            }
            var clients = new List<AgentRemoteClient>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var page = await runtime.ListRemoteClients(observation.EnvironmentId, cursor, bounded.Token);
                CheckGeneration(generation, runtime.CurrentGeneration);
                clients.AddRange(page.Clients);
                if (clients.Count > 1000 || (page.NextCursor is { } next && !cursors.Add(next)))
                    throw new IOException("Native client pagination exceeded its bound.");
                cursor = page.NextCursor;
            } while (cursor is not null);
            if (operation == RemoteOperation.Clients)
                return new(RemoteAccessOutcome.Success, "Remembered clients; IDs select individual revocation.", Status(), Clients: clients);
            if (!clients.Any(client => client.Id == clientId))
                return new(RemoteAccessOutcome.Rejected, "Selected client was absent from the current client list.", Status());
            issued = true;
            await runtime.RevokeRemoteClient(observation.EnvironmentId, clientId!, bounded.Token);
            CheckGeneration(generation, runtime.CurrentGeneration);
            return new(RemoteAccessOutcome.Success, "Selected client revocation confirmed.", Status());
        }
        catch (AgentRequestRejected)
        {
            return new(RemoteAccessOutcome.Rejected, "The runtime rejected the native-access request; check account access and provider availability.", Status());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            return new(issued ? RemoteAccessOutcome.Uncertain : RemoteAccessOutcome.Unavailable,
                issued ? "Native-access operation is unconfirmed; inspect status before another explicit attempt. Desired intent is retained."
                    : "Native access is unavailable; ordinary Discussion processing remains available.", Status(true));
        }
        finally { if (entered) gate.Release(); }
    }

    void CheckGeneration(int expected, int actual)
    {
        if (expected != actual || expected != runtime.CurrentGeneration) throw new IOException("Owned runtime changed.");
    }

    public void Dispose()
    {
        runtime.RemoteAccessChanged -= Observe;
        runtime.RemoteAccessReady -= Restore;
        gate.Dispose();
    }
}
