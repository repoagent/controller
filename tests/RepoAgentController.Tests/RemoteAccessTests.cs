using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class RemoteAccessTests
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;
    static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Owner_enables_connecting_runtime_then_pairs_only_after_connected_without_publishing_code_in_status()
    {
        var runtime = new RemoteRuntime { EnableConnection = AgentRemoteConnection.Connecting };
        var state = new InMemoryControllerState();
        using var remote = new RemoteAccess(runtime, state, new FakeTimeProvider(Now));
        var initial = await remote.Execute(RemoteOperation.Status, LocalCallerPermission.Administration, null, Cancel);
        Assert.False(initial.Status!.DesiredEnabled);
        Assert.Equal(AgentRemoteConnection.Disabled, initial.Status.Connection);
        Assert.Equal(RemoteAccessOutcome.Rejected, (await remote.Execute(RemoteOperation.Pair, LocalCallerPermission.Administration, null, Cancel)).Outcome);

        var enabled = await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        Assert.Equal(RemoteAccessOutcome.Success, enabled.Outcome);
        Assert.True(state.ReadRemoteAccessEnabled());
        Assert.Equal(AgentRemoteConnection.Connecting, enabled.Status!.Connection);
        Assert.Equal(RemoteAccessOutcome.Rejected, (await remote.Execute(RemoteOperation.Pair, LocalCallerPermission.Administration, null, Cancel)).Outcome);
        runtime.Observe(new(1, AgentRemoteConnection.Connected, "environment"));
        var paired = await remote.Execute(RemoteOperation.Pair, LocalCallerPermission.Administration, null, Cancel);
        Assert.Equal(RemoteAccessOutcome.Success, paired.Outcome);
        Assert.Equal("private-code", paired.Pair!.Code);
        Assert.Equal(Now.AddMinutes(5), paired.Pair.ExpiresAt);
        var status = await remote.Execute(RemoteOperation.Status, LocalCallerPermission.Administration, null, Cancel);
        Assert.Null(status.Pair);
        Assert.DoesNotContain("private-code", System.Text.Json.JsonSerializer.Serialize(status, ActivityJson.Default.RemoteAccessResult), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_inventory_follows_pages_and_revocation_targets_only_an_explicit_present_client()
    {
        var runtime = new RemoteRuntime();
        runtime.Pages[""] = new([new("client-a", "Desktop", null, null, null)], "page-2");
        runtime.Pages["page-2"] = new([new("client-b", "Laptop", null, null, null)], null);
        var state = new InMemoryControllerState();
        using var remote = new RemoteAccess(runtime, state, new FakeTimeProvider(Now));
        await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        var clients = await remote.Execute(RemoteOperation.Clients, LocalCallerPermission.Administration, null, Cancel);
        Assert.Collection(clients.Clients!, client => Assert.Equal("client-a", client.Id), client => Assert.Equal("client-b", client.Id));
        Assert.Equal(RemoteAccessOutcome.Rejected, (await remote.Execute(RemoteOperation.Revoke, LocalCallerPermission.Administration, "absent", Cancel)).Outcome);
        Assert.Empty(runtime.Revoked);
        Assert.Equal(RemoteAccessOutcome.Success, (await remote.Execute(RemoteOperation.Revoke, LocalCallerPermission.Administration, "client-b", Cancel)).Outcome);
        Assert.Equal("client-b", Assert.Single(runtime.Revoked));
        runtime.Failure = new AgentRequestRejected("private-provider-error");
        Assert.Equal(RemoteAccessOutcome.Rejected, (await remote.Execute(RemoteOperation.Revoke, LocalCallerPermission.Administration, "client-a", Cancel)).Outcome);
    }

    [Fact]
    public async Task Disable_records_intent_before_uncertain_provider_call_and_restores_disabled_after_restart()
    {
        var runtime = new RemoteRuntime();
        var state = new InMemoryControllerState();
        using var remote = new RemoteAccess(runtime, state, new FakeTimeProvider(Now));
        await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        runtime.BeforeDisable = () => Assert.False(state.ReadRemoteAccessEnabled());
        runtime.Failure = new IOException("private-provider-error");
        var disabled = await remote.Execute(RemoteOperation.Disable, LocalCallerPermission.Administration, null, Cancel);
        Assert.Equal(RemoteAccessOutcome.Uncertain, disabled.Outcome);
        Assert.False(disabled.Status!.DesiredEnabled);
        Assert.True(disabled.Status.Unconfirmed);
        Assert.DoesNotContain("private-provider-error", disabled.Message, StringComparison.Ordinal);
        runtime.Failure = null;
        await runtime.Restart(Cancel);
        var status = await remote.Execute(RemoteOperation.Status, LocalCallerPermission.Administration, null, Cancel);
        Assert.Equal(AgentRemoteConnection.Disabled, status.Status!.Connection);
        Assert.False(status.Status.DesiredEnabled);
        Assert.Equal(2, status.Status.Generation);
        Assert.Empty(runtime.Revoked);
    }

    [Fact]
    public async Task Enabled_intent_survives_controller_and_runtime_restart_and_stale_observations_are_ignored()
    {
        var runtime = new RemoteRuntime();
        var state = new InMemoryControllerState();
        using (var original = new RemoteAccess(runtime, state, new FakeTimeProvider(Now)))
            await original.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        using var restored = new RemoteAccess(runtime, state, new FakeTimeProvider(Now));
        await runtime.Restart(Cancel);
        runtime.Observe(new(1, AgentRemoteConnection.Disabled));
        var status = await restored.Execute(RemoteOperation.Status, LocalCallerPermission.Administration, null, Cancel);
        Assert.True(status.Status!.DesiredEnabled);
        Assert.Equal(2, status.Status.Generation);
        Assert.Equal(AgentRemoteConnection.Connected, status.Status.Connection);
    }

    [Fact]
    public async Task Pairing_timeout_is_uncertain_and_is_never_automatically_retried()
    {
        var clock = new FakeTimeProvider(Now);
        var runtime = new RemoteRuntime { DelayPair = true };
        using var remote = new RemoteAccess(runtime, new InMemoryControllerState(), clock);
        await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        var pairing = remote.Execute(RemoteOperation.Pair, LocalCallerPermission.Administration, null, Cancel);
        clock.Advance(TimeSpan.FromSeconds(21));
        var result = await pairing;
        Assert.Equal(RemoteAccessOutcome.Uncertain, result.Outcome);
        Assert.Null(result.Pair);
        Assert.Equal(1, runtime.PairAttempts);
        Assert.Equal(RemoteAccessOutcome.Success, (await remote.Execute(RemoteOperation.Status, LocalCallerPermission.Administration, null, Cancel)).Outcome);
        Assert.Equal(1, runtime.PairAttempts);
    }

    [Fact]
    public async Task Expired_pair_and_replaced_runtime_cannot_confirm_a_remote_operation()
    {
        var runtime = new RemoteRuntime { PairExpiry = Now.AddSeconds(-1) };
        using var remote = new RemoteAccess(runtime, new InMemoryControllerState(), new FakeTimeProvider(Now));
        await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        var expired = await remote.Execute(RemoteOperation.Pair, LocalCallerPermission.Administration, null, Cancel);
        Assert.Equal(RemoteAccessOutcome.Uncertain, expired.Outcome);
        Assert.Null(expired.Pair);
        runtime.ReplaceOnEnable = true;
        var replaced = await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel);
        Assert.Equal(RemoteAccessOutcome.Uncertain, replaced.Outcome);
        Assert.True(replaced.Status!.Unconfirmed);
    }

    [Fact]
    public async Task Unauthorized_and_unknown_commands_and_failed_intent_write_cannot_enable_access()
    {
        var runtime = new RemoteRuntime();
        var state = new InMemoryControllerState();
        using var remote = new RemoteAccess(runtime, state, new FakeTimeProvider(Now));
        foreach (var operation in Enum.GetValues<RemoteOperation>())
            foreach (var permission in new[] { LocalCallerPermission.Unknown, LocalCallerPermission.Inspection, (LocalCallerPermission)999 })
                Assert.Equal(RemoteAccessOutcome.Forbidden, (await remote.Execute(operation, permission, null, Cancel)).Outcome);
        Assert.Equal(RemoteAccessOutcome.Forbidden, (await remote.Execute((RemoteOperation)999, LocalCallerPermission.Administration, null, Cancel)).Outcome);
        state.FailWrites = true;
        Assert.Equal(RemoteAccessOutcome.Unavailable, (await remote.Execute(RemoteOperation.Enable, LocalCallerPermission.Administration, null, Cancel)).Outcome);
        Assert.False(state.ReadRemoteAccessEnabled());
        Assert.Equal(AgentRemoteConnection.Disabled, runtime.Status.Connection);
    }
}

sealed class RemoteRuntime : IAgentRemoteAccess
{
    public int CurrentGeneration { get; private set; } = 1;
    public event Action<AgentRemoteAccessObservation>? RemoteAccessChanged;
    public event Func<CancellationToken, Task>? RemoteAccessReady;
    public AgentRemoteAccessObservation Status { get; private set; } = new(1, AgentRemoteConnection.Disabled);
    public AgentRemoteConnection EnableConnection { get; init; } = AgentRemoteConnection.Connected;
    public Dictionary<string, AgentRemoteClients> Pages { get; } = new() { [""] = new([], null) };
    public List<string> Revoked { get; } = [];
    public Exception? Failure { get; set; }
    public Action? BeforeDisable { get; set; }
    public bool DelayPair { get; init; }
    public bool ReplaceOnEnable { get; set; }
    public int PairAttempts { get; private set; }
    public DateTimeOffset PairExpiry { get; init; } = new(2026, 10, 31, 12, 5, 0, TimeSpan.Zero);

    public void Observe(AgentRemoteAccessObservation observation)
    {
        if (observation.Generation == CurrentGeneration) Status = observation;
        RemoteAccessChanged?.Invoke(observation);
    }
    public async Task Restart(CancellationToken ct)
    {
        CurrentGeneration++;
        Status = new(CurrentGeneration, AgentRemoteConnection.Disabled);
        if (RemoteAccessReady is { } ready) await ready(ct);
    }
    public Task<AgentRemoteAccessObservation> ReadRemoteAccess(CancellationToken ct)
    {
        if (Failure is { } failure) throw failure;
        return Task.FromResult(Status);
    }
    public Task<AgentRemoteAccessObservation> EnableRemoteAccess(CancellationToken ct)
    {
        if (Failure is { } failure) throw failure;
        var observation = new AgentRemoteAccessObservation(CurrentGeneration, EnableConnection, "environment");
        if (ReplaceOnEnable) CurrentGeneration++;
        Observe(observation);
        return Task.FromResult(observation);
    }
    public Task<AgentRemoteAccessObservation> DisableRemoteAccess(CancellationToken ct)
    {
        BeforeDisable?.Invoke();
        if (Failure is { } failure) throw failure;
        Observe(new(CurrentGeneration, AgentRemoteConnection.Disabled));
        return Task.FromResult(Status);
    }
    public async Task<AgentRemotePair> PairRemoteAccess(CancellationToken ct)
    {
        PairAttempts++;
        if (DelayPair) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        if (Failure is { } failure) throw failure;
        return new("private-code", PairExpiry);
    }
    public Task<AgentRemoteClients> ListRemoteClients(string environmentId, string? cursor, CancellationToken ct) => Task.FromResult(Pages[cursor ?? ""]);
    public Task RevokeRemoteClient(string environmentId, string clientId, CancellationToken ct)
    {
        if (Failure is { } failure) throw failure;
        Revoked.Add(clientId);
        return Task.CompletedTask;
    }
}
