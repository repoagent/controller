using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Public controller enrollment: start (admission, the verifier's GitHub token and discovery), status, completion once
// GitHub shows the approval, and the controller's acknowledgment. Each answer is the HTTP status and, for 200, the body.
public sealed class EnrollmentService(EnrollmentStore store, IGitHubTokens tokens, IAppInstallations installations,
    IControllerApprovals approvals, Uri? brokerOrigin, TimeProvider clock)
{
    private readonly ApprovalObserver observer = new(installations, approvals, clock);
    private readonly GitHubTokenMinter minter = new(tokens, clock);

    public Task<IssuanceResult> StartAsync(EnrollmentRequest request, string? secret, CancellationToken caller) =>
        StartAsync(() => EnrollmentInput.From(request), secret, caller);

    // Setup v2 binds a repository Agent variable; setup v3 binds a repository Actions variable.
    public Task<IssuanceResult> StartRepositoryVariableAsync(RepositoryVariableEnrollmentRequest request, string? secret, CancellationToken caller, int setupVersion = 2) =>
        StartAsync(() => EnrollmentInput.From(request, setupVersion), secret, caller);

    private Task<IssuanceResult> StartAsync(Func<EnrollmentInput> input, string? secret, CancellationToken caller) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var request = input();
        if (brokerOrigin is null) throw new IssuanceDenied();
        var hash = DeviceSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        var s = await store.AdmitAsync(request, Convert.ToHexString(hash), work.Cancellation);
        if (s.Pending.Marker is not null) return Status(s);
        var existing = s.Pending.Attempt is not null ? await store.AttemptAsync(s, work.Cancellation) : null;
        if (existing is not null) IssuanceStore.RequireAttemptPayload(existing.Value);
        if (existing is not null && Text(existing.Value.State) == "settled")
        {
            await store.ReleaseUnsentAttemptAsync(s, existing, work.Cancellation);
            existing = null;
        }
        if (existing is not null && Text(existing.Value.State) is not ("known" or "reserved")) throw new IssuanceDenied();
        // A definitely unsent reservation can resume. A sending/unknown outcome never retries POST.
        var unsent = existing is not null && Text(existing.Value.State) == "reserved";
        if (unsent && Date(existing!.Value.JwtExpiresAt) <= clock.GetUtcNow()) throw new IssuanceDenied();
        var reserved = await store.ReserveWorkAsync(s, existing is null, work);
        // Retry admission pays for new work, but an unsent mint keeps its original signed authority.
        var reservation = unsent ? existing : reserved.Attempt;
        var app = await observer.AuthorizeAsync(reservation is null ? reserved.Window : IssuanceStore.ReservedWindow(reservation.Value), work.Cancellation);
        var installation = await observer.DiscoverAsync(s.ApprovalRequest(), app, work);
        s.Pending.InstallationId = installation;
        if (existing is null || unsent)
        {
            var attempt = reservation!;
            attempt.Value.InstallationId = installation;
            await store.CommitAsync(s, [attempt], work.Cancellation);
            if (Date(attempt.Value.JwtExpiresAt) <= clock.GetUtcNow()) throw new IssuanceDenied();
            attempt.Value.State = "sending";
            attempt.Value.SendClaimedAt = clock.GetUtcNow();
            await store.CommitAsync(s, [attempt], work.Cancellation);
            KnownGitHubToken? known = null;
            try
            {
                known = await minter.MintAsync(app, installation, new RepositoryIdentity(Id(s.RepositoryId), Text(s.Pending.RequestedRepository)),
                    RuntimeAuthority.Verifier(s.Approval), work.Cancellation);
                attempt.Value.ProtectedToken = store.ProtectToken(s, attempt.Value, known.Token.Value);
                attempt.Value.ExpiresAt = known.Token.ExpiresAt;
                attempt.Value.ScopeValid = known.ScopeValid;
                attempt.Value.State = "known";
                await store.CommitAsync(s, [attempt], work.Cancellation);
                if (!known.ScopeValid) throw new IssuanceDenied();
                existing = attempt;
            }
            catch (Exception ex) when (Answerable(ex))
            {
                if (known is not null) await RevokeKnownAsync(s, attempt, known.Token);
                throw;
            }
        }
        var token = store.Recover(s, existing!);
        var started = clock.GetUtcNow();
        var binding = await observer.BindAsync(s.ApprovalRequest(), token, work);
        s.Pending.Repository = binding.Repository;
        if (s.Approval == ApprovalContract.Legacy)
        {
            s.Pending.Environment = binding.Environment!;
            s.Pending.EnvironmentId = binding.EnvironmentId!.Value;
        }
        s.Pending.Marker = s.CreateMarker();
        s.Pending.State = "pending";
        store.RequireLive(s.Pending);
        work.Fresh(started);
        await store.FinishAsync(s, [existing!], work.Cancellation);
        return Status(s);
    });

    public Task<IssuanceResult> StatusAsync(string id, string? secret, CancellationToken caller, int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var s = await store.SnapshotAsync(id, Convert.ToHexString(DeviceSecret.Hash(secret) ?? throw new IssuanceDenied(401)), work.Cancellation, setupVersion);
        return Status(s);
    });

    public Task<IssuanceResult> CompleteAsync(string id, string? secret, CancellationToken caller, int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var s = await store.SnapshotAsync(id, Convert.ToHexString(DeviceSecret.Hash(secret) ?? throw new IssuanceDenied(401)), work.Cancellation, setupVersion);
        store.RequireLive(s.Pending);
        if (s.Pending.Marker is null) throw new IssuanceDenied(409);
        var attempt = await store.AttemptAsync(s, work.Cancellation);
        Tracked<ControllerEnrollment>? enrollment = null;
        if (Text(s.Pending.State) == "completed") enrollment = await EnrollmentAsync(s, work.Cancellation);
        Barrier(s, attempt, enrollment is not null);
        var observation = await ObserveAsync(s, attempt, work);
        if (!observation.Approved) return Status(s);
        var completion = s.Pending.CompletionId;
        if (enrollment is null)
        {
            completion = Guid.NewGuid().ToString("N");
            var controllerSecret = EnrollmentSecrets.Generate();
            var p = s.Pending;
            var approval = s.Approval;
            var record = new ControllerEnrollment
            {
                RepositoryId = p.RepositoryId,
                Repository = p.Repository,
                EnrollmentId = p.EnrollmentId,
                Epoch = p.Epoch,
                InstallationId = p.InstallationId,
                Variable = p.Variable,
                Profile = p.Profile,
                Challenge = p.Challenge,
                Marker = p.Marker,
                ApprovalKind = approval.Kind,
                SetupVersion = approval.SetupVersion,
                PolicyVersion = approval.Policy,
                AppId = AppId,
                AppSlug = AppSlug,
                GitHubHost = "github.com",
                ControllerSecretHash = Convert.ToHexString(DeviceSecret.Hash(controllerSecret)!),
                Acknowledged = false,
                State = "unacknowledged",
                ExpiresAt = Date(p.Deadline),
                OutstandingTokenCount = 0,
                CompletionId = completion,
                PendingId = s.Id
            };
            if (approval == ApprovalContract.Legacy)
            {
                record.Environment = p.Environment;
                record.EnvironmentId = p.EnvironmentId;
            }
            enrollment = new(ControllerEnrollment.Key(s.PartitionRepositoryId, s.EnrollmentId), record, null);
            var envelopeFields = new Dictionary<string, object>
            {
                ["protocolVersion"] = approval.RuntimeVersion,
                ["brokerOrigin"] = brokerOrigin!.GetLeftPart(UriPartial.Authority),
                ["githubHost"] = "github.com",
                ["deviceId"] = s.EnrollmentId,
                ["deviceSecret"] = controllerSecret,
                ["appId"] = AppId,
                ["appSlug"] = AppSlug,
                ["installationId"] = Number(p.InstallationId),
                ["repositoryId"] = Id(s.RepositoryId),
                ["repository"] = Text(p.Repository),
                ["permissions"] = RuntimeAuthority.For(approval, Text(p.Profile)).Permissions
            };
            if (approval.NamesProfile)
            {
                envelopeFields["profile"] = Text(p.Profile);
                envelopeFields["policyVersion"] = approval.Policy;
            }
            var envelope = JsonSerializer.Serialize(envelopeFields);
            p.ProtectedCompletion = store.ProtectCompletion(s, envelope);
            p.CompletionId = completion;
            p.State = "completed";
            s.Gate.OwnerEnrollmentId = s.EnrollmentId;
            s.Gate.State = "unacknowledged";
            s.Gate.VerifierAttempt = Text(attempt.Value.OperationId);
        }
        store.RequireLive(s.Pending);
        work.Fresh(observation.Started);
        await store.FinishAsync(s, [attempt, enrollment], work.Cancellation);
        await store.PublishLocatorAsync(s, work.Cancellation);
        store.RequireLive(s.Pending);
        work.Fresh(observation.Started);
        var result = JsonSerializer.Deserialize<JsonElement>(store.UnprotectCompletion(s, Text(s.Pending.ProtectedCompletion)));
        return new(200, new { completionId = completion, enrollment = result });
    });

    public Task<IssuanceResult> AcknowledgeAsync(string deviceId, string completionId, string? secret, CancellationToken caller, int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var hash = DeviceSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        if (!DeviceSecret.IsDeviceId(deviceId) || !Guid.TryParseExact(completionId, "N", out _)) throw new IssuanceDenied(401);
        var locator = await store.LocatorAsync(deviceId, work.Cancellation) ?? throw new IssuanceDenied(401);
        if (Text(locator.EnrollmentId) != deviceId) throw new IssuanceDenied(401);
        var s = await store.SnapshotAsync(Text(locator.PendingId), null, work.Cancellation, setupVersion);
        if (s.EnrollmentId != deviceId || s.RepositoryId != Text(locator.RepositoryId)) throw new IssuanceDenied(401);
        var enrollment = await EnrollmentAsync(s, work.Cancellation);
        var e = enrollment.Value;
        if (!DeviceSecret.Matches(hash, Text(e.ControllerSecretHash)) || Text(e.CompletionId) != completionId
            || Text(s.Pending.CompletionId) != completionId) throw new IssuanceDenied(401);
        if (e.Acknowledged == true)
        {
            var grant = EnrollmentStore.Grant(s, enrollment);
            grant.RequireBinding(store.Epoch);
            grant.RequireActive(clock.GetUtcNow());
            return new(200, new { state = "active" }); // No write or lifetime extension on a lost-ack retry.
        }
        store.RequireLive(s.Pending);
        var attempt = await store.AttemptAsync(s, work.Cancellation);
        Barrier(s, attempt, true);
        var observation = await ObserveAsync(s, attempt, work);
        if (!observation.Approved) return Status(s);
        if (Text(e.State) != "unacknowledged" || Text(s.Pending.State) != "completed") throw new IssuanceDenied(401);
        store.RequireLive(s.Pending);
        work.Fresh(observation.Started);
        e.Acknowledged = true;
        e.State = "active";
        e.LastActivityAt = clock.GetUtcNow();
        e.ExpiresAt = clock.GetUtcNow().AddDays(90);
        s.Gate.State = "active";
        s.Pending.State = "active";
        s.Pending.FirstTerminalAt ??= clock.GetUtcNow();
        s.Pending.SetupSecretHash = null;
        s.Pending.ProtectedCompletion = null;
        await store.FinishAsync(s, [enrollment, attempt], work.Cancellation);
        return new(200, new { state = "active" });
    });

    private async Task<ApprovalObservation> ObserveAsync(PendingSnapshot s, Tracked<IssuanceAttempt> attempt, IssuanceWork work)
    {
        var token = store.Recover(s, attempt);
        var reservation = await store.ReserveWorkAsync(s, false, work);
        var app = await observer.AuthorizeAsync(reservation.Window, work.Cancellation);
        ApprovalObservation observation;
        try { observation = await observer.ObserveAsync(s.ApprovalRequest(), app, token, work); }
        catch (ApprovalChanged)
        {
            if (s.Pending.PositiveApproval == true) await CloseAsync(s, attempt, work.Cancellation);
            throw new IssuanceDenied(409);
        }
        if (!observation.Approved)
        {
            if (s.Pending.PositiveApproval == true) await CloseAsync(s, attempt, work.Cancellation);
            else await store.FinishAsync(s, [attempt], work.Cancellation);
        }
        else s.Pending.PositiveApproval = true;
        return observation;
    }

    private async Task CloseAsync(PendingSnapshot s, Tracked<IssuanceAttempt> attempt, CancellationToken ct)
    {
        var extra = new List<TrackedRecord> { attempt };
        if (s.Gate.OwnerEnrollmentId == s.EnrollmentId)
        {
            var enrollment = await EnrollmentAsync(s, ct);
            enrollment.Value.State = "revoked";
            s.Gate.State = "closed";
            enrollment.Value.FirstTerminalAt ??= clock.GetUtcNow();
            extra.Add(enrollment);
            extra.Add(new Tracked<RevocationReceipt>(RevocationReceipt.Key(s.PartitionRepositoryId, s.EnrollmentId), new()
            {
                RepositoryId = s.RepositoryId,
                EnrollmentId = s.EnrollmentId,
                Epoch = store.Epoch,
                RevokedAt = clock.GetUtcNow()
            }, null));
        }
        s.Pending.State = "closed";
        s.Pending.FirstTerminalAt ??= clock.GetUtcNow();
        s.Pending.ProtectedCompletion = null;
        await store.FinishAsync(s, extra, ct);
    }

    // The controller enrollment the pending enrollment completed, bound to it and to this deployment.
    private async Task<Tracked<ControllerEnrollment>> EnrollmentAsync(PendingSnapshot s, CancellationToken ct)
    {
        var enrollment = await store.EnrollmentAsync(s, ct) ?? throw new IssuanceDenied(401);
        var e = enrollment.Value;
        if (Text(e.PendingId) != s.Id || Text(e.EnrollmentId) != s.EnrollmentId || Text(e.RepositoryId) != s.RepositoryId
            || Text(e.Epoch) != store.Epoch) throw new IssuanceDenied(401);
        var grant = EnrollmentStore.Grant(s, enrollment);
        if (grant.Approval != s.Approval) throw new IssuanceDenied();
        if (Text(e.Marker) != Text(s.Pending.Marker) || Text(e.Profile) != Text(s.Pending.Profile)
            || Number(e.InstallationId) != Number(s.Pending.InstallationId)) throw new IssuanceDenied();
        grant.RequireBinding(store.Epoch);
        return enrollment;
    }

    // Completion and acknowledgment use only the pending enrollment's own verifier, while it is the gate's only token.
    private void Barrier(PendingSnapshot s, Tracked<IssuanceAttempt> attempt, bool completed)
    {
        _ = store.Recover(s, attempt);
        if (Number(s.Gate.OutstandingTokenCount) != 1 || Text(attempt.Value.PendingId) != s.Id) throw new IssuanceDenied(409);
        if (completed)
        {
            if (s.Gate.OwnerEnrollmentId != s.EnrollmentId || Text(s.Gate.State) != "unacknowledged"
                || s.Gate.VerifierAttempt != Text(attempt.Value.OperationId)) throw new IssuanceDenied(409);
        }
        else if (s.Gate.OwnerEnrollmentId is not null || Text(s.Gate.State) != "vacant") throw new IssuanceDenied(409);
    }

    private IssuanceResult Status(PendingSnapshot s) => new(200, new
    {
        pendingId = s.Id,
        state = Date(s.Pending.Deadline) <= clock.GetUtcNow() && Text(s.Pending.State) != "active" ? "expired" : Text(s.Pending.State),
        marker = s.Pending.Marker,
        deadline = Date(s.Pending.Deadline)
    });

    private async Task RevokeKnownAsync(PendingSnapshot s, Tracked<IssuanceAttempt> attempt, GitHubToken token)
    {
        // Finite finalization allowance after the operation deadline; no fire-and-forget work.
        using var allowance = new CancellationTokenSource(TimeSpan.FromSeconds(6), clock);
        try
        {
            await minter.RevokeAsync(token, allowance.Token);
            IssuanceStore.RequireAttemptPayload(attempt.Value);
            attempt.Value.FirstTerminalAt ??= clock.GetUtcNow();
            attempt.Value.State = "settled";
            attempt.Value.SettledAt ??= clock.GetUtcNow();
            attempt.Value.ProtectedToken = null;
            await store.CommitAsync(s, [attempt], allowance.Token);
        }
        catch (Exception ex) when (Answerable(ex)) { /* Retain unresolved evidence; never compensate counters. */ }
    }

    private async Task<IssuanceResult> Run(Func<IssuanceWork, Task<IssuanceResult>> action)
    {
        using var work = new IssuanceWork(clock);
        try { return await action(work); }
        catch (Exception ex) when (Answerable(ex)) { return new(ex is IssuanceDenied denied ? denied.Status : 503); }
    }
}
