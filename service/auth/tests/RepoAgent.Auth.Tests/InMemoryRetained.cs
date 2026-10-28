using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The setup v2 (Agent variable) journeys and the retained rows the cleanup, reclamation and redaction cases share, over
// the in-memory broker. Each journey step is the core operation the route calls.
internal static class Retained
{
    public const string Repository = InMemoryBroker.RepositoryId;

    public static RepositoryVariableEnrollmentRequest AgentRequest(string id, string profile = "polling") =>
        new(id, 123456, "owner/project", "APPROVAL", profile);

    public sealed record Journey(string Id, string Pending, string EnrollmentId, string Secret, string Completion);

    // POST /v2/enrollments with GitHub configured for the profile; the Agent variable then holds the marker.
    public static async Task<(string Id, string Pending)> StartAgent(InMemoryBroker f, string profile = "polling", string? id = null)
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f.GitHub, profile);
        id ??= Guid.NewGuid().ToString("N");
        var start = await f.Start(AgentRequest(id, profile), 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        f.GitHub.Marker = start.Json.GetProperty("marker").GetString()!;
        return (id, start.Json.GetProperty("pendingId").GetString()!);
    }

    // Explicit historical-state construction for recovery/quota tests: earlier brokers admitted competing verifiers.
    // Current admission cannot produce this state. Temporarily hide the old gate authority through the fake state
    // port, run the real enrollment writer, then restore that authority and its count. Never used by readiness tests.
    public static async Task<Answer> HistoricalDiscovery(InMemoryBroker f, Func<Task<Answer>> start)
    {
        var key = RepositoryGate.Key(Repository);
        var old = f.State.Get(key);
        if (old is null) return await start();
        f.State.Change(key, row =>
        {
            row["State"] = "vacant";
            row["OutstandingTokenCount"] = 0L;
            row.Remove("OwnerDeviceId");
            row.Remove("VerifierAttempt");
        });
        try { return await start(); }
        finally
        {
            f.State.Change(key, row =>
            {
                row["State"] = old.State!;
                row["OutstandingTokenCount"] = (long)row["OutstandingTokenCount"] + old.OutstandingTokenCount!.Value;
                if (old.OwnerEnrollmentId is { } owner) row["OwnerDeviceId"] = owner;
                else row.Remove("OwnerDeviceId");
                if (old.VerifierAttempt is { } verifier) row["VerifierAttempt"] = verifier;
                else row.Remove("VerifierAttempt");
            });
        }
    }

    public static async Task<(string Id, string Pending)> HistoricalAgent(InMemoryBroker f, string profile = "polling")
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f.GitHub, profile);
        var id = Guid.NewGuid().ToString("N");
        var start = await HistoricalDiscovery(f, () => f.Start(AgentRequest(id, profile), 2));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        f.GitHub.Marker = start.Json.GetProperty("marker").GetString()!;
        return (id, start.Json.GetProperty("pendingId").GetString()!);
    }

    // Started and completed one minute later; not acknowledged.
    public static async Task<Journey> CompletedAgent(InMemoryBroker f, string profile = "polling")
    {
        var (id, pending) = await StartAgent(f, profile);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var enrollment = completion.Json.GetProperty("enrollment");
        return new(id, pending, enrollment.GetProperty("deviceId").GetString()!, enrollment.GetProperty("deviceSecret").GetString()!,
            completion.Json.GetProperty("completionId").GetString()!);
    }

    // Completed and acknowledged, a minute apart, with a minute passed after.
    public static async Task<Journey> ActiveAgent(InMemoryBroker f, string profile = "polling")
    {
        var journey = await CompletedAgent(f, profile);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(journey.EnrollmentId, journey.Completion, journey.Secret, 2)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return journey;
    }

    public static Versioned<T>? Read<T>(this InMemoryBrokerState state, StateKey<T> key) where T : BrokerRecord, new() =>
        state.Get(key) is { } value ? new(value, state.Version(key)!) : null;

    public static Versioned<EnrollmentLedger> EnrollmentLedger(InMemoryBroker f, string id) =>
        f.State.Read(RepoAgent.Auth.EnrollmentLedger.Key(id)) ?? throw new InvalidOperationException("No enrollment ledger " + id);

    // The retained issuance ledgers ("operation:{operation ID}"), in ordinal name order.
    public static List<Versioned<IssuanceLedger>> IssuanceLedgers(InMemoryBroker f) =>
        [.. f.State.Rows(IssuanceCapacity.Scope).Where(row => row.Name.StartsWith("operation:", StringComparison.Ordinal))
            .Select(row => f.State.Read(IssuanceLedger.Key(row.Name["operation:".Length..]))!)];

    public static StateKey<IssuanceAttempt> Attempt(IssuanceLedger ledger) => IssuanceAttempt.Key(Repository, ledger.OperationId!);

    public static Task ReclaimIssuance(InMemoryBroker f, IssuanceLedger ledger, IBrokerState? state = null) =>
        f.Reclamation(state).ReclaimIssuanceAsync(ledger.OperationId!, ledger.Generation!, Xunit.TestContext.Current.CancellationToken);

    public static Task ReclaimEnrollment(InMemoryBroker f, string id, IBrokerState? state = null) =>
        f.Reclamation(state).ReclaimEnrollmentAsync(id, EnrollmentLedger(f, id).Value.Generation!, Xunit.TestContext.Current.CancellationToken);
}
