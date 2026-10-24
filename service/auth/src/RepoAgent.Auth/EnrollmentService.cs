using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;
using static RepoAgent.Auth.EnrollmentStore;

namespace RepoAgent.Auth;

public sealed class EnrollmentService(EnrollmentStore store, GitHubIssuance github, AppJwtSigner signer, BrokerOptions options, TimeProvider clock)
{
    private readonly EnrollmentGitHub provider = new(github, clock);

    public Task<IssuanceResult> StartAsync(EnrollmentRequest request, string? secret, CancellationToken caller) =>
        StartAsync(() => EnrollmentInput.From(request), secret, caller);

    // Setup v2 binds a repository Agent variable; setup v3 binds a repository Actions variable.
    public Task<IssuanceResult> StartRepositoryVariableAsync(RepositoryVariableEnrollmentRequest request, string? secret, CancellationToken caller, int setupVersion = 2) =>
        StartAsync(() => EnrollmentInput.From(request, setupVersion), secret, caller);

    private Task<IssuanceResult> StartAsync(Func<EnrollmentInput> input, string? secret, CancellationToken caller) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var request = input();
        if (options.BrokerOrigin is null) throw new IssuanceDenied();
        var hash = DeviceSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        var s = await store.AdmitAsync(request, Convert.ToHexString(hash), work.Cancellation);
        if (s.Pending.ContainsKey("Marker")) return Status(s);
        var existing = s.Pending.ContainsKey("Attempt") ? await store.AttemptAsync(s, work.Cancellation) : null;
        if (existing is not null) PayloadClock.RequireAttempt(existing);
        if (existing is not null && Text(existing, "State") == "settled")
        {
            await store.ReleaseUnsentAttemptAsync(s, existing, work.Cancellation);
            existing = null;
        }
        if (existing is not null && Text(existing, "State") is not ("known" or "reserved")) throw new IssuanceDenied();
        // A definitely unsent reservation can resume. A sending/unknown outcome never retries POST.
        if (existing is not null && Text(existing, "State") == "reserved" && Date(existing, "JwtExpiresAt") <= clock.GetUtcNow()) throw new IssuanceDenied();
        var reservation = await store.ReserveWorkAsync(s, existing is null, work);
        // Retry admission pays for new work, but an unsent mint keeps its original signed authority.
        if (existing is not null && Text(existing, "State") == "reserved") reservation = existing;
        var jwt = await signer.SignAsync(reservation, work.Cancellation);
        var installation = await provider.DiscoverAsync(s, jwt, work);
        s.Pending["InstallationId"] = installation;
        if (existing is null || Text(existing, "State") == "reserved")
        {
            reservation["InstallationId"] = installation;
            await store.CommitAsync(s, [Action(reservation)], work.Cancellation);
            if (Date(reservation, "JwtExpiresAt") <= clock.GetUtcNow()) throw new IssuanceDenied();
            reservation["State"] = "sending";
            reservation["SendClaimedAt"] = clock.GetUtcNow();
            await store.CommitAsync(s, [Action(reservation)], work.Cancellation);
            IssuedToken? known = null;
            try
            {
                known = await github.MintAsync(s.RepositoryId, Text(s.Pending, "RequestedRepository"), installation, s.Approval.VerifierProfile, jwt, work.Cancellation);
                reservation["ProtectedToken"] = store.ProtectToken(s, reservation, known.Token);
                reservation["ExpiresAt"] = known.ExpiresAt;
                reservation["ScopeValid"] = known.ScopeValid;
                reservation["State"] = "known";
                await store.CommitAsync(s, [Action(reservation)], work.Cancellation);
                if (!known.ScopeValid) throw new IssuanceDenied();
                existing = reservation;
            }
            catch (Exception ex) when (Expected(ex))
            {
                if (known is not null) await RevokeKnownAsync(s, reservation, known.Token);
                throw;
            }
        }
        var token = store.Recover(s, existing!);
        var started = clock.GetUtcNow();
        var binding = await provider.BindAsync(s, token, work);
        s.Pending["Repository"] = binding.Repository;
        if (s.Approval == ApprovalContract.Legacy)
        {
            s.Pending["Environment"] = binding.Environment!;
            s.Pending["EnvironmentId"] = binding.EnvironmentId!.Value;
        }
        s.Pending["Marker"] = ApprovalMarker.Create(new(s.Gate, s.Pending));
        s.Pending["State"] = "pending";
        store.RequireLive(s.Pending);
        work.Fresh(started);
        await store.FinishAsync(s, [Action(existing!)], work.Cancellation);
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
        if (!s.Pending.ContainsKey("Marker")) throw new IssuanceDenied(409);
        var attempt = await store.AttemptAsync(s, work.Cancellation);
        TableEntity? device = null;
        if (Text(s.Pending, "State") == "completed") device = await DeviceAsync(s, work.Cancellation);
        Barrier(s, attempt, device is not null);
        var observation = await ObserveAsync(s, attempt, work);
        if (!observation.Approved) return Status(s);
        var completion = s.Pending.GetString("CompletionId");
        if (device is null)
        {
            completion = Guid.NewGuid().ToString("N");
            var deviceSecret = EnrollmentSecrets.Generate();
            device = new(s.Partition, "device:" + s.DeviceId);
            foreach (var key in new[] { "RepositoryId", "Repository", "DeviceId", "Epoch", "InstallationId", "Variable", "Profile", "Challenge", "Marker" }) device[key] = s.Pending[key];
            if (s.Approval == ApprovalContract.Legacy)
            {
                device["Environment"] = s.Pending["Environment"];
                device["EnvironmentId"] = s.Pending["EnvironmentId"];
            }
            s.Approval.Write(device);
            device["AppId"] = AppId; device["AppSlug"] = AppSlug; device["GitHubHost"] = "github.com";
            device["SecretHash"] = Convert.ToHexString(DeviceSecret.Hash(deviceSecret)!);
            device["Acknowledged"] = false; device["State"] = "unacknowledged";
            device["ExpiresAt"] = Date(s.Pending, "Deadline"); device["OutstandingTokenCount"] = 0L;
            device["CompletionId"] = completion; device["PendingId"] = s.Id;
            var envelopeFields = new Dictionary<string, object>
            {
                ["protocolVersion"] = s.Approval.RuntimeVersion, ["brokerOrigin"] = options.BrokerOrigin!.GetLeftPart(UriPartial.Authority), ["githubHost"] = "github.com",
                ["deviceId"] = s.DeviceId, ["deviceSecret"] = deviceSecret, ["appId"] = AppId, ["appSlug"] = AppSlug,
                ["installationId"] = Number(s.Pending, "InstallationId"), ["repositoryId"] = Id(s.RepositoryId),
                ["repository"] = Text(s.Pending, "Repository"), ["permissions"] = Permissions(Text(s.Pending, "Profile"))
            };
            if (s.Approval.NamesProfile)
            {
                envelopeFields["profile"] = Text(s.Pending, "Profile");
                envelopeFields["policyVersion"] = s.Approval.Policy;
            }
            var envelope = JsonSerializer.Serialize(envelopeFields);
            s.Pending["ProtectedCompletion"] = store.ProtectCompletion(s, envelope);
            s.Pending["CompletionId"] = completion!;
            s.Pending["State"] = "completed";
            s.Gate["OwnerDeviceId"] = s.DeviceId;
            s.Gate["State"] = "unacknowledged";
            s.Gate["VerifierAttempt"] = Text(attempt, "OperationId");
        }
        store.RequireLive(s.Pending);
        work.Fresh(observation.Started);
        await store.FinishAsync(s, [Action(attempt), Action(device, string.IsNullOrEmpty(device.ETag.ToString()))], work.Cancellation);
        await store.PublishLocatorAsync(s, work.Cancellation);
        store.RequireLive(s.Pending);
        work.Fresh(observation.Started);
        var result = JsonSerializer.Deserialize<JsonElement>(store.UnprotectCompletion(s, Text(s.Pending, "ProtectedCompletion")));
        return new(200, new { completionId = completion, enrollment = result });
    });

    public Task<IssuanceResult> AcknowledgeAsync(string deviceId, string completionId, string? secret, CancellationToken caller, int setupVersion = 1) => Run(async work =>
    {
        caller.ThrowIfCancellationRequested();
        var hash = DeviceSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        if (!DeviceSecret.IsDeviceId(deviceId) || !Guid.TryParseExact(completionId, "N", out _)) throw new IssuanceDenied(401);
        var locator = await store.ReadAsync("locator:" + deviceId, "locator", work.Cancellation) ?? throw new IssuanceDenied(401);
        if (Text(locator, "DeviceId") != deviceId) throw new IssuanceDenied(401);
        var s = await store.SnapshotAsync(Text(locator, "PendingId"), null, work.Cancellation, setupVersion);
        if (s.DeviceId != deviceId || s.RepositoryId != Text(locator, "RepositoryId")) throw new IssuanceDenied(401);
        var device = await DeviceAsync(s, work.Cancellation);
        if (!DeviceSecret.Matches(hash, Text(device, "SecretHash")) || Text(device, "CompletionId") != completionId
            || Text(s.Pending, "CompletionId") != completionId) throw new IssuanceDenied(401);
        if (device.GetBoolean("Acknowledged") == true)
        {
            new GrantSnapshot(s.Gate, device).RequireBinding(options);
            new GrantSnapshot(s.Gate, device).RequireActive(clock.GetUtcNow());
            return new(200, new { state = "active" }); // No write or lifetime extension on a lost-ack retry.
        }
        store.RequireLive(s.Pending);
        var attempt = await store.AttemptAsync(s, work.Cancellation);
        Barrier(s, attempt, true);
        var observation = await ObserveAsync(s, attempt, work);
        if (!observation.Approved) return Status(s);
        if (Text(device, "State") != "unacknowledged" || Text(s.Pending, "State") != "completed") throw new IssuanceDenied(401);
        store.RequireLive(s.Pending);
        work.Fresh(observation.Started);
        device["Acknowledged"] = true; device["State"] = "active";
        device["LastActivityAt"] = clock.GetUtcNow(); device["ExpiresAt"] = clock.GetUtcNow().AddDays(90);
        s.Gate["State"] = "active";
        s.Pending["State"] = "active";
        PayloadClock.FirstTerminal(s.Pending, clock.GetUtcNow());
        s.Pending.Remove("RecoveryHash"); s.Pending.Remove("ProtectedCompletion");
        await store.FinishAsync(s, [Action(device), Action(attempt)], work.Cancellation);
        return new(200, new { state = "active" });
    });

    private async Task<(bool Approved, DateTimeOffset Started)> ObserveAsync(EnrollmentSnapshot s, TableEntity attempt, IssuanceWork work)
    {
        var token = store.Recover(s, attempt);
        var reservation = await store.ReserveWorkAsync(s, false, work);
        var jwt = await signer.SignAsync(reservation, work.Cancellation);
        (bool Approved, DateTimeOffset Started) observation;
        try { observation = await provider.ObserveAsync(s, jwt, token, work); }
        catch (ApprovalChanged)
        {
            if (s.Pending.GetBoolean("PositiveApproval") == true) await CloseAsync(s, attempt, work.Cancellation);
            throw new IssuanceDenied(409);
        }
        if (!observation.Approved)
        {
            if (s.Pending.GetBoolean("PositiveApproval") == true) await CloseAsync(s, attempt, work.Cancellation);
            else await store.FinishAsync(s, [Action(attempt)], work.Cancellation);
        }
        else s.Pending["PositiveApproval"] = true;
        return observation;
    }
    private async Task CloseAsync(EnrollmentSnapshot s, TableEntity attempt, CancellationToken ct)
    {
        var extra = new List<TableTransactionAction> { Action(attempt) };
        if (s.Gate.GetString("OwnerDeviceId") == s.DeviceId)
        {
            var device = await DeviceAsync(s, ct);
            device["State"] = "revoked"; s.Gate["State"] = "closed";
            PayloadClock.FirstTerminal(device, clock.GetUtcNow());
            extra.Add(Action(device));
            var receipt = new TableEntity(s.Partition, "revocation:" + s.DeviceId)
            { ["RepositoryId"] = s.RepositoryId, ["DeviceId"] = s.DeviceId, ["Epoch"] = options.DeploymentEpoch, ["RevokedAt"] = clock.GetUtcNow() };
            extra.Add(Action(receipt, true));
        }
        s.Pending["State"] = "closed";
        PayloadClock.FirstTerminal(s.Pending, clock.GetUtcNow());
        s.Pending.Remove("ProtectedCompletion");
        await store.FinishAsync(s, extra, ct);
    }
    private async Task<TableEntity> DeviceAsync(EnrollmentSnapshot s, CancellationToken ct)
    {
        var device = await store.ReadAsync(s.Partition, "device:" + s.DeviceId, ct) ?? throw new IssuanceDenied(401);
        if (Text(device, "PendingId") != s.Id || Text(device, "DeviceId") != s.DeviceId || Text(device, "RepositoryId") != s.RepositoryId
            || Text(device, "Epoch") != options.DeploymentEpoch) throw new IssuanceDenied(401);
        s.Approval.Require(device);
        if (Text(device, "Marker") != Text(s.Pending, "Marker") || Text(device, "Profile") != Text(s.Pending, "Profile")
            || Number(device, "InstallationId") != Number(s.Pending, "InstallationId")) throw new IssuanceDenied();
        new GrantSnapshot(s.Gate, device).RequireBinding(options);
        return device;
    }
    private void Barrier(EnrollmentSnapshot s, TableEntity attempt, bool completed)
    {
        _ = store.Recover(s, attempt);
        if (Number(s.Gate, "OutstandingTokenCount") != 1 || Text(attempt, "PendingId") != s.Id) throw new IssuanceDenied(409);
        if (completed)
        {
            if (s.Gate.GetString("OwnerDeviceId") != s.DeviceId || Text(s.Gate, "State") != "unacknowledged"
                || s.Gate.GetString("VerifierAttempt") != Text(attempt, "OperationId")) throw new IssuanceDenied(409);
        }
        else if (s.Gate.ContainsKey("OwnerDeviceId") || Text(s.Gate, "State") != "vacant") throw new IssuanceDenied(409);
    }
    private IssuanceResult Status(EnrollmentSnapshot s) => new(200, new
    {
        pendingId = s.Id, state = Date(s.Pending, "Deadline") <= clock.GetUtcNow() && Text(s.Pending, "State") != "active" ? "expired" : Text(s.Pending, "State"),
        marker = s.Pending.GetString("Marker"), deadline = Date(s.Pending, "Deadline")
    });
    private async Task RevokeKnownAsync(EnrollmentSnapshot s, TableEntity attempt, string token)
    {
        using var allowance = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            await github.RevokeAsync(token, allowance.Token);
            PayloadClock.RequireAttempt(attempt);
            PayloadClock.FirstTerminal(attempt, clock.GetUtcNow());
            attempt["State"] = "settled";
            if (!attempt.ContainsKey("SettledAt")) attempt["SettledAt"] = clock.GetUtcNow();
            attempt.Remove("ProtectedToken");
            await store.CommitAsync(s, [Action(attempt)], allowance.Token);
        }
        catch (Exception ex) when (Expected(ex)) { /* Retain unresolved evidence; never compensate counters. */ }
    }
    private async Task<IssuanceResult> Run(Func<IssuanceWork, Task<IssuanceResult>> action)
    {
        using var work = new IssuanceWork(clock);
        try { return await action(work); }
        catch (Exception ex) when (Expected(ex)) { return new(ex is IssuanceDenied denied ? denied.Status : 503); }
    }
    private static bool Expected(Exception ex) => ex is IssuanceDenied or ApprovalChanged or RequestFailedException or AuthenticationFailedException
        or HttpRequestException or IOException or InvalidDataException or InvalidCastException or FormatException or JsonException
        or OverflowException or OperationCanceledException or CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException
        or SecretProtectionFailed;
}
