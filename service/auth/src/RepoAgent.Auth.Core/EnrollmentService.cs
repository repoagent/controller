using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Original wire protocols, fresh schema 2 state. Only the short setup completion is protected;
// neither runtime nor internal verifier GitHub tokens are stored.
public sealed class EnrollmentService(CurrentRepositoryStore store, IGitHubTokens tokens, IAppInstallations installations,
    IControllerApprovals approvals, ISecretProtection secrets, Uri? brokerOrigin, TimeProvider clock)
{
    private readonly ApprovalObserver observer = new(installations, approvals, clock);
    public Task<IssuanceResult> StartAsync(EnrollmentRequest request, string? secret, CancellationToken caller) =>
        StartAsync(() => EnrollmentInput.From(request), secret, caller);
    public Task<IssuanceResult> StartRepositoryVariableAsync(RepositoryVariableEnrollmentRequest request, string? secret,
        CancellationToken caller, int setupVersion = 2) => StartAsync(() => EnrollmentInput.From(request, setupVersion), secret, caller);
    private Task<IssuanceResult> StartAsync(Func<EnrollmentInput> input, string? secret, CancellationToken caller) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        if (brokerOrigin is null) throw new IssuanceDenied();
        var request = input();
        var hash = Convert.ToHexString(ControllerSecret.Hash(secret) ?? throw new IssuanceDenied(401));
        var read = await store.StartAsync(request, hash, work.Cancellation);
        RequireLive(read.Value);
        if (read.Value.Marker is not null) return Status(read.Value);
        read = await store.ChargeAsync(read, 1, work.Cancellation);
        var row = read.Value;
        await using var credentials = new RequestTokens(tokens, installations, clock);
        var app = await credentials.AuthorizeAsync(work.Cancellation);
        row.InstallationId = await observer.DiscoverAsync(row.Request(), app, work);
        var verifier = await credentials.MintAsync(app, Number(row.InstallationId), row.Identity,
            RuntimeAuthority.Verifier(row.Approval), work.Cancellation);
        var binding = await observer.BindAsync(row.Request(), verifier, work);
        row.Repository = binding.Repository;
        row.Environment = binding.Environment;
        row.EnvironmentId = binding.EnvironmentId;
        row.Marker = row.CreateMarker();
        RequireLive(row);
        caller.ThrowIfCancellationRequested();
        await store.SaveAsync(read, work.Cancellation);
        return Status(row);
    });
    public Task<IssuanceResult> StatusAsync(string id, string? secret, CancellationToken caller, int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var read = await PendingAsync(id, secret, setupVersion, work.Cancellation);
        return Status(read.Value);
    });
    public Task<IssuanceResult> CompleteAsync(string id, string? secret, CancellationToken caller, int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var read = await PendingAsync(id, secret, setupVersion, work.Cancellation);
        RequireLive(read.Value);
        if (read.Value.Marker is null) throw new IssuanceDenied(409);
        read = await store.ChargeAsync(read, 1, work.Cancellation);
        var row = read.Value;
        var observation = await ApprovedAsync(read, work);
        if (!observation.Approved) return Status(row);
        work.Fresh(observation.Started);
        if (row.State != "completed")
        {
            var controllerSecret = EnrollmentSecrets.Generate();
            row.ControllerSecretHash = Convert.ToHexString(ControllerSecret.Hash(controllerSecret)!);
            row.CompletionId = Guid.NewGuid().ToString("N");
            row.ProtectedCompletion = secrets.Protect(Purpose(row), Envelope(row, controllerSecret));
            row.ExpiresAt = row.Deadline;
            row.State = "completed";
        }
        RequireLive(row);
        caller.ThrowIfCancellationRequested();
        await store.SaveAsync(read, work.Cancellation);
        var envelope = JsonSerializer.Deserialize<JsonElement>(secrets.Unprotect(Purpose(row), Text(row.ProtectedCompletion)));
        return new(200, new { completionId = row.CompletionId, enrollment = envelope });
    });
    public Task<IssuanceResult> AcknowledgeAsync(string id, string completionId, string? secret, CancellationToken caller,
        int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var hash = ControllerSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        var read = await store.LoadAsync(id, work.Cancellation);
        var row = read.Value;
        if (row.EnrollmentId != id || row.State is not ("completed" or "active") || row.Approval.SetupVersion != setupVersion
            || row.CompletionId != completionId || !ControllerSecret.Matches(hash, Text(row.ControllerSecretHash))) throw new IssuanceDenied(401);
        if (row.State == "active")
        {
            if (Date(row.ExpiresAt) <= clock.GetUtcNow()) throw new IssuanceDenied(401);
            return new(200, new { state = "active" });
        }
        RequireLive(row);
        read = await store.ChargeAsync(read, 1, work.Cancellation);
        row = read.Value;
        var observation = await ApprovedAsync(read, work);
        if (!observation.Approved) return Status(row);
        work.Fresh(observation.Started);
        RequireLive(row);
        caller.ThrowIfCancellationRequested();
        row.State = "active";
        row.ExpiresAt = row.NextCleanupAt = clock.GetUtcNow().AddDays(90);
        row.SetupSecretHash = row.ProtectedCompletion = row.RequestHash = row.PendingId = null;
        row.Deadline = null;
        row.PositiveApproval = null;
        await store.SaveAsync(read, work.Cancellation);
        return new(200, new { state = "active" });
    });
    private async Task<Versioned<CurrentRepository>> PendingAsync(string id, string? secret, int setupVersion, CancellationToken ct)
    {
        var hash = ControllerSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        var read = await store.LoadAsync(id, ct);
        var row = read.Value;
        if (row.PendingId != id || row.State is not ("pending" or "completed") || row.Approval.SetupVersion != setupVersion
            || !ControllerSecret.Matches(hash, Text(row.SetupSecretHash))) throw new IssuanceDenied(401);
        return read;
    }
    private async Task<(bool Approved, DateTimeOffset Started)> ApprovedAsync(Versioned<CurrentRepository> read, IssuanceWork work)
    {
        var row = read.Value;
        await using var credentials = new RequestTokens(tokens, installations, clock);
        var app = await credentials.AuthorizeAsync(work.Cancellation);
        var verifier = await credentials.MintAsync(app, Number(row.InstallationId), row.Identity,
            RuntimeAuthority.Verifier(row.Approval), work.Cancellation);
        try
        {
            var observation = await observer.ObserveAsync(row.Request(), app, verifier, work);
            work.Fresh(observation.Started);
            if (observation.Approved) { row.PositiveApproval = true; return (true, observation.Started); }
            if (row.PositiveApproval == true) { row.Close(); await store.SaveAsync(read, work.Cancellation); throw new IssuanceDenied(409); }
            return (false, observation.Started);
        }
        catch (ApprovalChanged)
        {
            row.Close();
            await store.SaveAsync(read, work.Cancellation);
            throw new IssuanceDenied(409);
        }
    }
    private void RequireLive(CurrentRepository row)
    {
        CurrentRepositoryStore.RequirePending(row);
        if (Date(row.Deadline) <= clock.GetUtcNow()) throw new IssuanceDenied(401);
    }
    private IssuanceResult Status(CurrentRepository row) => new(200, new
    {
        pendingId = row.PendingId,
        state = Date(row.Deadline) <= clock.GetUtcNow() ? "expired" : row.State,
        marker = row.Marker,
        deadline = row.Deadline
    });
    private static SecretPurpose Purpose(CurrentRepository row) => row.Approval.CompletionPurpose(
        Number(row.RepositoryId).ToString(System.Globalization.CultureInfo.InvariantCulture), Text(row.Epoch), Text(row.PendingId));
    private string Envelope(CurrentRepository row, string secret)
    {
        var approval = row.Approval;
        var fields = new Dictionary<string, object>
        {
            ["protocolVersion"] = approval.RuntimeVersion,
            ["brokerOrigin"] = brokerOrigin!.GetLeftPart(UriPartial.Authority),
            ["githubHost"] = "github.com",
            [approval.CanonicalNames ? "enrollmentId" : "deviceId"] = Text(row.EnrollmentId),
            [approval.CanonicalNames ? "controllerSecret" : "deviceSecret"] = secret,
            ["appId"] = AppId,
            ["appSlug"] = AppSlug,
            ["installationId"] = Number(row.InstallationId),
            ["repositoryId"] = Number(row.RepositoryId),
            ["repository"] = Text(row.Repository),
            ["permissions"] = RuntimeAuthority.For(approval, Text(row.Profile)).Permissions
        };
        if (approval.NamesProfile) { fields["profile"] = Text(row.Profile); fields["policyVersion"] = approval.Policy; }
        return JsonSerializer.Serialize(fields);
    }
    private async Task<IssuanceResult> Run(Func<IssuanceWork, Task<IssuanceResult>> action)
    {
        using var work = new IssuanceWork(clock);
        try { return await action(work); }
        catch (Exception ex) when (Answerable(ex))
        { return ex is IssuanceDenied denied ? new(denied.Status) { Rejection = denied.Reason } : new(503); }
    }
}
