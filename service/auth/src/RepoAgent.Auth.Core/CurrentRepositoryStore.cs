using System.Globalization;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

public sealed class CurrentRepositoryStore(IBrokerState state, string epoch, TimeProvider clock,
    IssuanceLimits? configuredLimits = null, int repositoryLimit = 128)
{
    private readonly IssuanceLimits limits = configuredLimits ?? new();
    public string Epoch => epoch;

    public static long RepositoryOf(string id)
    {
        var parts = id.Split('_');
        return parts is [var repository, var random] && Guid.TryParseExact(random, "N", out _)
            ? Id(repository) : throw new IssuanceDenied(401);
    }

    public async Task<Versioned<CurrentRepository>> LoadAsync(string id, CancellationToken ct)
    {
        var legacy = LegacyEnrollmentLocator.IsLegacyId(id);
        var repository = legacy
            ? Number((await state.ReadAsync(LegacyEnrollmentLocator.Key(id), ct) ?? throw new IssuanceDenied(401)).Value.RepositoryId)
            : RepositoryOf(id);
        var read = await state.ReadAsync(CurrentRepository.Key(repository), ct) ?? throw new IssuanceDenied(401);
        read.Value.Validate(repository, epoch);
        if (legacy && read.Value.EnrollmentId != id) throw new IssuanceDenied(401);
        return read;
    }

    public async Task<Versioned<CurrentRepository>> AuthenticateAsync(string id, string? secret, int runtimeVersion, CancellationToken ct)
    {
        var hash = ControllerSecret.Hash(secret) ?? throw new IssuanceDenied(401);
        var read = await LoadAsync(id, ct);
        var row = read.Value;
        if (row.EnrollmentId != id || row.State != "active" || row.Approval.RuntimeVersion != runtimeVersion
            || Date(row.ExpiresAt) <= clock.GetUtcNow() || !ControllerSecret.Matches(hash, Text(row.ControllerSecretHash)))
            throw new IssuanceDenied(401);
        return read;
    }

    // Reserve a complete request's maximum GitHub work before any external call. Both quota and
    // repository counters change in one transaction; a failed/uncertain request keeps its minute charge.
    public async Task<Versioned<CurrentRepository>> ChargeAsync(Versioned<CurrentRepository> read, int mints, CancellationToken ct)
    {
        var global = await QuotaAsync(ct);
        var row = read.Value;
        Charge(global.Value, row, mints);
        var versions = await state.CommitAsync(new StateTransaction()
            .Replace(CurrentBrokerQuota.Key, global.Value, global.Version)
            .Replace(CurrentRepository.Key(Number(row.RepositoryId)), row, read.Version), ct);
        return new(row, versions[1] ?? throw new IssuanceDenied());
    }

    internal async Task<Versioned<CurrentRepository>> StartAsync(EnrollmentInput request, string hash, CancellationToken ct)
    {
        var key = CurrentRepository.Key(request.RepositoryId);
        var read = await state.ReadAsync(key, ct);
        if (read is not null)
        {
            read.Value.Validate(request.RepositoryId, Text(read.Value.Epoch));
            var old = read.Value;
            if (old.Epoch == epoch && old.PendingId == request.RepositoryId.ToString(CultureInfo.InvariantCulture) + "_" + request.IdempotencyId)
            {
                if (old.SetupSecretHash != hash || old.RequestHash != request.BodyHash) throw new IssuanceDenied(409);
                RequirePending(old);
                return read;
            }
            if (old.Epoch == epoch && old.State != "closed" && Date(old.NextCleanupAt) > clock.GetUtcNow()) throw new IssuanceDenied(409);
        }
        var global = await QuotaAsync(ct);
        if (read is null)
        {
            if (Number(global.Value.Repositories) >= repositoryLimit) throw new IssuanceDenied(429, BrokerRejectionReason.CandidateRepositories);
            global.Value.Repositories = Number(global.Value.Repositories) + 1;
        }
        var contract = request.Approval;
        var repository = request.RepositoryId.ToString(CultureInfo.InvariantCulture);
        var deadline = clock.GetUtcNow().Add(PendingLifetime);
        var row = new CurrentRepository
        {
            SchemaVersion = 2,
            RepositoryId = request.RepositoryId,
            Epoch = epoch,
            State = "pending",
            EnrollmentId = repository + "_" + Guid.NewGuid().ToString("N"),
            PendingId = repository + "_" + request.IdempotencyId,
            RequestHash = request.BodyHash,
            SetupSecretHash = hash,
            Repository = request.Repository,
            Profile = request.Profile,
            Variable = request.Variable,
            Environment = request.Environment,
            ApprovalKind = contract.Kind,
            SetupVersion = contract.SetupVersion,
            PolicyVersion = contract.Policy,
            Challenge = EnrollmentSecrets.Generate(),
            Deadline = deadline,
            NextCleanupAt = deadline,
            Window = read?.Value.Window ?? 0,
            Admissions = read?.Value.Admissions ?? 0,
            Reads = read?.Value.Reads ?? 0,
            Signs = read?.Value.Signs ?? 0,
            Mints = read?.Value.Mints ?? 0
        };
        // Start's provider charge is made by ChargeAsync immediately before discovery/mint.
        var tx = new StateTransaction().Replace(CurrentBrokerQuota.Key, global.Value, global.Version);
        if (read is null) tx.Add(key, row); else tx.Replace(key, row, read.Version);
        if (read is not null) await RemoveLegacyLocatorAsync(tx, read.Value, ct);
        var versions = await state.CommitAsync(tx, ct);
        return new(row, versions[1] ?? throw new IssuanceDenied());
    }

    public static void RequirePending(CurrentRepository row)
    {
        if (row.State is not ("pending" or "completed") || row.Deadline is null) throw new IssuanceDenied(401);
    }

    public async Task<Versioned<CurrentRepository>> SaveAsync(Versioned<CurrentRepository> read, CancellationToken ct)
    {
        var transaction = new StateTransaction().Replace(CurrentRepository.Key(Number(read.Value.RepositoryId)), read.Value, read.Version);
        if (read.Value.State == "closed") await RemoveLegacyLocatorAsync(transaction, read.Value, ct);
        var version = await state.CommitAsync(transaction, ct);
        return new(read.Value, version[0] ?? throw new IssuanceDenied());
    }

    private async Task RemoveLegacyLocatorAsync(StateTransaction transaction, CurrentRepository row, CancellationToken ct)
    {
        if (!LegacyEnrollmentLocator.IsLegacyId(row.EnrollmentId)) return;
        var key = LegacyEnrollmentLocator.Key(Text(row.EnrollmentId));
        if (await state.ReadAsync(key, ct) is { } locator)
        {
            if (locator.Value.RepositoryId != row.RepositoryId) throw new IssuanceDenied();
            transaction.Delete(key, locator.Version);
        }
    }

    public async Task<Versioned<CurrentBrokerQuota>> QuotaAsync(CancellationToken ct)
    {
        var read = await state.ReadAsync(CurrentBrokerQuota.Key, ct) ?? throw new IssuanceDenied();
        if (read.Value.SchemaVersion != 2 || Number(read.Value.Repositories) > 128) throw new IssuanceDenied();
        foreach (var counter in new[] { read.Value.Window, read.Value.Admissions, read.Value.Reads, read.Value.Signs, read.Value.Mints }) _ = Number(counter);
        return read;
    }

    private void Charge(CurrentBrokerQuota global, CurrentRepository row, int mints)
    {
        var window = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        if (Number(global.Window) > window || Number(row.Window) > window) throw new IssuanceDenied();
        if (global.Window != window) { global.Window = window; global.Admissions = global.Reads = global.Signs = global.Mints = 0; }
        if (row.Window != window) { row.Window = window; row.Admissions = row.Reads = row.Signs = row.Mints = 0; }
        if (Number(global.Admissions) + 1 > limits.GlobalAdmissions || Number(global.Reads) + 8 > limits.GlobalReads
            || Number(global.Signs) + 1 > limits.GlobalSigns || Number(global.Mints) + mints > limits.GlobalMints)
            throw new IssuanceDenied(429, BrokerRejectionReason.GlobalMinuteBudget);
        if (Number(row.Admissions) + 1 > limits.RepositoryAdmissions || Number(row.Reads) + 8 > limits.RepositoryReads
            || Number(row.Signs) + 1 > limits.RepositorySigns || Number(row.Mints) + mints > limits.RepositoryMints)
            throw new IssuanceDenied(429, BrokerRejectionReason.RepositoryMinuteBudget);
        global.Admissions++; global.Reads += 8; global.Signs++; global.Mints += mints;
        row.Admissions++; row.Reads += 8; row.Signs++; row.Mints += mints;
    }
}
