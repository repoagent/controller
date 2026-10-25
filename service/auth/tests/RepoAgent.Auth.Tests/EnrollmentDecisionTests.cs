using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The enrollment decisions the end-to-end cases do not reach on their own: competing writers during admission, the
// limits each work claim charges, records that are not the pending enrollment's own, and what completion, closing and
// acknowledgment write. Each case runs the core over the in-memory broker state.
public sealed class EnrollmentDecisionTests
{
    private static Task Lost(string what) => throw new BrokerStateUnavailable(what);

    [Theory]
    [InlineData("global:enrollment-v1")]
    [InlineData(InMemoryBroker.Partition)]
    public async Task Admission_reevaluates_a_competing_commit_and_charges_once(string scope)
    {
        var f = new InMemoryBroker();
        // The repository gate exists, so the second admission replaces it.
        await Start(f);
        var injected = false;
        f.State.BeforeCommit = transaction =>
        {
            if (injected || transaction.Writes[0].Key.Scope != scope) return Task.CompletedTask;
            injected = true;
            // Another admission changed the quota, or the gate, after this one read it.
            f.State.Change(scope == InMemoryBroker.Partition ? RepositoryGate.Key(InMemoryBroker.RepositoryId) : EnrollmentQuota.Key, _ => { });
            return Task.CompletedTask;
        };
        var start = await f.Start(InMemoryBroker.Request());
        Assert.True(injected);
        Assert.Equal(1, f.State.Conflicts);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(2, f.State.Get(EnrollmentQuota.Key)!.Count);
        Assert.Equal(2, f.State.Get(CandidateRepository.Key(InMemoryBroker.RepositoryId))!.PendingCount);
        Assert.Equal(2, f.Gate.PendingCount);
    }

    [Theory]
    [InlineData("PendingCapacityReleased")]
    [InlineData("RetirementState")]
    public async Task Released_or_retired_ledger_is_never_published_again(string field)
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        // The ledger commits and its response is lost, so no pending enrollment exists yet.
        f.State.AfterCommit = _ => Lost("Lost committed admission");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.State.AfterCommit = null;
        f.State.Change(EnrollmentLedger.Key(request.IdempotencyId), row => row[field] = field == "RetirementState" ? "retiring" : true);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        Assert.DoesNotContain(f.Rows(), row => row.Scope == InMemoryBroker.Partition);
        Assert.Equal(0, f.GitHub.Reads);
    }

    // An expired ledger is gone, even after reclamation released it: the expiry answers before the release does.
    [Fact]
    public async Task Retried_start_of_an_expired_released_ledger_is_gone()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        f.State.AfterCommit = _ => Lost("Lost committed admission");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.State.AfterCommit = null;
        // Reclamation released the expired ledger; redaction has not run yet.
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        f.State.Change(EnrollmentLedger.Key(request.IdempotencyId), row => row["PendingCapacityReleased"] = true);
        Assert.Equal(HttpStatusCode.Gone, (await f.Start(request)).StatusCode);
        Assert.DoesNotContain(f.Rows(), row => row.Scope == InMemoryBroker.Partition);
        Assert.Equal(0, f.GitHub.Reads);
    }

    // The gate and the candidate repository count pending enrollments in separate partitions; each limit holds on its own.
    [Fact]
    public async Task Gate_pending_limit_holds_when_the_candidate_repository_counts_fewer()
    {
        var f = new InMemoryBroker();
        await Start(f);
        f.State.Change(RepositoryGate.Key(InMemoryBroker.RepositoryId), row => row["PendingCount"] = 4L);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Single(f.Rows(), row => row.Scope == InMemoryBroker.Partition && row.Name.StartsWith("pending:", StringComparison.Ordinal));
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Gate_of_another_deployment_epoch_never_takes_a_new_pending_enrollment()
    {
        var f = new InMemoryBroker();
        await Start(f);
        f.DeploymentEpoch = "restored-epoch";
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Single(f.Rows(), row => row.Scope == InMemoryBroker.Partition && row.Name.StartsWith("pending:", StringComparison.Ordinal));
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Theory]
    [InlineData("candidates")]
    [InlineData("pending")]
    public async Task Inconsistent_enrollment_counts_deny_admission_without_a_charge(string corruption)
    {
        var f = new InMemoryBroker();
        await Start(f);
        if (corruption == "candidates") f.State.Change(EnrollmentQuota.Key, row => row["CandidateCount"] = 0L);
        else f.State.Change(CandidateRepository.Key(InMemoryBroker.RepositoryId), row => row["PendingCount"] = 2L);
        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
    }

    [Theory]
    [InlineData("GlobalAdmissions")]
    [InlineData("RepositoryAdmissions")]
    [InlineData("RepositoryAttempts")]
    public async Task Each_work_claim_charges_the_global_and_repository_limits(string limit)
    {
        var f = new InMemoryBroker
        {
            Limits = limit switch
            {
                "GlobalAdmissions" => new(GlobalAdmissions: 1),
                "RepositoryAdmissions" => new(RepositoryAdmissions: 1),
                _ => new(RepositoryAttempts: 1),
            }
        };
        Assert.Equal(HttpStatusCode.OK, (await f.Start(InMemoryBroker.Request())).StatusCode);
        // Admissions are charged per minute; the outstanding verifier counts until it settles.
        if (limit == "RepositoryAttempts") f.Clock.Advance(TimeSpan.FromMinutes(1));
        var (mints, authorizations) = (f.GitHub.Mints, f.GitHub.Authorizations);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Equal((mints, authorizations), (f.GitHub.Mints, f.GitHub.Authorizations));
    }

    [Fact]
    public async Task Verifier_occupies_a_counted_global_slot_and_the_count_limits_the_next()
    {
        var f = new InMemoryBroker { Limits = new(GlobalAttempts: 2) };
        Assert.Equal(HttpStatusCode.OK, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Equal(1, f.State.Get(IssuanceQuota.Key)!.Slots);
        // Another verifier holds slot 5, with its retained ledger, above a limit an operator lowered after it was charged.
        var (slot, ledger) = (f.State.Get(IssuanceSlot.Key(0))!, f.State.Get(IssuanceLedger.Key(f.State.Get(IssuanceSlot.Key(0))!.OperationId!))!);
        slot.OperationId = ledger.OperationId = Guid.NewGuid().ToString("N");
        slot.GlobalSlot = ledger.GlobalSlot = 5;
        f.State.Seed(IssuanceSlot.Key(5), slot);
        f.State.Seed(IssuanceLedger.Key(ledger.OperationId), ledger);
        f.State.Change(IssuanceQuota.Key, row => (row["Slots"], row["RetainedIssuanceCount"]) = (2L, 2L));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var mints = f.GitHub.Mints;
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Equal(mints, f.GitHub.Mints);
        Assert.Null(f.State.Get(IssuanceSlot.Key(1)));
    }

    [Fact]
    public async Task Unknown_mint_outcome_is_never_replayed_or_charged_again()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Unknown GitHub mint outcome");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.GitHub.BeforeMint = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        var github = (f.GitHub.Authorizations, f.GitHub.Reads, f.GitHub.Mints);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        Assert.Equal(github, (f.GitHub.Authorizations, f.GitHub.Reads, f.GitHub.Mints));
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
    }

    [Fact]
    public async Task Redacted_known_verifier_is_never_used_or_charged_for_a_retried_start()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        // The verifier is known, but discovering the approval environment failed, so the start publishes no marker.
        var environment = f.GitHub.Environment;
        f.GitHub.Environment = new GitHubRead<ApprovalEnvironment>.Refused();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.GitHub.Environment = environment;
        var attempt = Assert.Single(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal));
        Assert.Equal("known", attempt.Read<IssuanceAttempt>().State);
        f.State.Change(IssuanceAttempt.Key(InMemoryBroker.RepositoryId, attempt.Read<IssuanceAttempt>().OperationId!), row => row["PayloadRedacted"] = true);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var github = (f.GitHub.Authorizations, f.GitHub.Reads);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        Assert.Equal(github, (f.GitHub.Authorizations, f.GitHub.Reads));
    }

    [Fact]
    public async Task Pending_enrollment_without_a_marker_cannot_complete()
    {
        var f = new InMemoryBroker();
        f.GitHub.Environment = new GitHubRead<ApprovalEnvironment>.Refused();
        var start = await f.Start(InMemoryBroker.Request());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        var pending = Assert.Single(f.Rows(), row => row.Scope == InMemoryBroker.Partition && row.Name.StartsWith("pending:", StringComparison.Ordinal))
            .Read<PendingControllerEnrollment>();
        Assert.Null(pending.Marker);
        f.GitHub.Environment = GitHubRead.Found(new ApprovalEnvironment(987, "approval/environment"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(pending.PendingId!)).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
    }

    [Fact]
    public async Task Stale_binding_read_never_publishes_the_marker()
    {
        var f = new InMemoryBroker();
        f.GitHub.BeforeRead = read =>
        {
            // The environment answer arrives 11 s after the binding started: outside freshness (10 s).
            if (read.StartsWith("environment ", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(InMemoryBroker.Request())).StatusCode);
        var pending = Assert.Single(f.Rows(), row => row.Scope == InMemoryBroker.Partition && row.Name.StartsWith("pending:", StringComparison.Ordinal));
        Assert.Null(pending.Read<PendingControllerEnrollment>().Marker);
    }

    // An unsent verifier resumes only inside its reserved JWT window, and only while the send claim still lands inside it.
    [Fact]
    public async Task Resumed_unsent_verifier_is_not_sent_once_its_window_closes_during_discovery()
    {
        var f = new InMemoryBroker();
        // The first commit that adds the verifier attempt is the persisted mint reservation; its response is lost.
        f.State.AfterCommit = transaction => transaction.Writes.Any(write => write.Kind == StateWriteKind.Add
            && write.Key.Name.StartsWith("attempt:", StringComparison.Ordinal)) ? Lost("Lost reserved verifier response") : Task.CompletedTask;
        var request = InMemoryBroker.Request();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.State.AfterCommit = null;
        var attempt = Assert.Single(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal)).Read<IssuanceAttempt>();
        Assert.Equal("reserved", attempt.State);
        // Resume 5 s before the reserved window ends; discovery takes 6 s.
        f.Clock.Advance(attempt.JwtExpiresAt!.Value.AddSeconds(-5) - f.Clock.GetUtcNow());
        f.GitHub.BeforeRead = read =>
        {
            if (read.StartsWith("installation ", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(6));
            return Task.CompletedTask;
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        Assert.Equal(0, f.GitHub.Mints);
        var stored = f.State.Get(IssuanceAttempt.Key(InMemoryBroker.RepositoryId, attempt.OperationId!))!;
        Assert.Equal("reserved", stored.State);
        Assert.Null(stored.SendClaimedAt);
    }

    [Fact]
    public async Task Expired_unsent_verifier_window_is_never_charged_again()
    {
        var f = new InMemoryBroker();
        // The first commit that adds the verifier attempt is the persisted mint reservation; its response is lost.
        f.State.AfterCommit = transaction => transaction.Writes.Any(write => write.Kind == StateWriteKind.Add
            && write.Key.Name.StartsWith("attempt:", StringComparison.Ordinal)) ? Lost("Lost reserved verifier response") : Task.CompletedTask;
        var request = InMemoryBroker.Request();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromSeconds(120));
        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(0, f.GitHub.Authorizations);
    }

    [Theory]
    [InlineData("unsent", HttpStatusCode.OK)]
    [InlineData("revoked", HttpStatusCode.ServiceUnavailable)]
    [InlineData("sent", HttpStatusCode.ServiceUnavailable)]
    public async Task Only_a_definitely_unsent_settled_verifier_frees_the_pending_enrollment_for_a_new_one(string settlement, HttpStatusCode expected)
    {
        var f = new InMemoryBroker();
        // The first commit that adds the verifier attempt is the persisted mint reservation; its response is lost.
        f.State.AfterCommit = transaction => transaction.Writes.Any(write => write.Kind == StateWriteKind.Add
            && write.Key.Name.StartsWith("attempt:", StringComparison.Ordinal)) ? Lost("Lost reserved verifier response") : Task.CompletedTask;
        var request = InMemoryBroker.Request();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        f.State.AfterCommit = null;
        var attempt = Assert.Single(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal)).Read<IssuanceAttempt>();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        // Reclamation settled the reservation as it settles an unsent one.
        f.State.Change(IssuanceAttempt.Key(InMemoryBroker.RepositoryId, attempt.OperationId!), row =>
        {
            row["State"] = "settled";
            row["CapacityReleased"] = true;
            row["SettlementReason"] = settlement == "revoked" ? "revoked" : "unsent";
            row["SettledAt"] = f.Clock.GetUtcNow();
            if (settlement == "sent") row["SendClaimedAt"] = attempt.ReservedAt!.Value;
        });
        Assert.Equal(expected, (await f.Start(request)).StatusCode);
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, f.GitHub.Mints);
    }

    [Theory]
    [InlineData("DeviceId")]
    [InlineData("Epoch")]
    [InlineData("ApprovalKind")]
    [InlineData("InstallationId")]
    [InlineData("ExpiresAt")]
    public async Task Verifier_that_is_not_the_pending_enrollments_own_and_usable_denies_completion_before_GitHub_work(string field)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        f.State.Change(IssuanceAttempt.Key(InMemoryBroker.RepositoryId, f.Pending(pending).Attempt!), row => row[field] = field switch
        {
            "DeviceId" => Guid.NewGuid().ToString("N"),
            "Epoch" => "restored-epoch",
            "ApprovalKind" => "repository-agent-variable",
            "InstallationId" => 790L,
            _ => (object)f.Clock.GetUtcNow().AddMinutes(4),
        });
        var (reads, authorizations) = (f.GitHub.Reads, f.GitHub.Authorizations);
        var completion = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, completion.StatusCode);
        Assert.Equal((reads, authorizations), (f.GitHub.Reads, f.GitHub.Authorizations));
        Assert.DoesNotContain(f.Rows(), row => row.Name.StartsWith("device:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PayloadRedacted")]
    [InlineData("State")]
    public async Task Redacted_pending_enrollment_is_never_completed(string field)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        f.State.Change(InMemoryBroker.PendingKey(pending), row => row[field] = field == "State" ? "redacted" : true);
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Complete(pending)).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
    }

    [Fact]
    public async Task Stored_marker_its_binding_does_not_encode_denies_before_GitHub_work()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var copied = f.GitHub.Marker.Replace("owner/project", "owner/copied", StringComparison.Ordinal);
        Assert.NotEqual(f.GitHub.Marker, copied);
        f.State.Change(InMemoryBroker.PendingKey(pending), row => row["Marker"] = copied);
        f.GitHub.Marker = copied;
        var reads = f.GitHub.Reads;
        var completion = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, completion.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.DoesNotContain("deviceSecret", completion.Text);
    }

    [Fact]
    public async Task Stale_approval_read_writes_no_controller_enrollment()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        f.GitHub.BeforeRead = read =>
        {
            if (read.StartsWith("variables ", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        var completion = await f.Complete(pending);
        Assert.NotEqual(HttpStatusCode.OK, completion.StatusCode);
        Assert.DoesNotContain(f.Rows(), row => row.Name.StartsWith("device:", StringComparison.Ordinal) || row.Scope.StartsWith("locator:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Completion_writes_an_unacknowledged_enrollment_that_expires_with_the_pending_deadline_and_an_advancing_gate()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var sequences = new List<long>();
        f.State.AfterCommit = transaction =>
        {
            if (transaction.Writes.Any(write => write.Key.Name == "gate")) sequences.Add(f.Gate.Sequence!.Value);
            return Task.CompletedTask;
        };
        var completion = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var device = f.Enrollment(completion.Json.GetProperty("enrollment").GetProperty("deviceId").GetString()!);
        Assert.Equal(("unacknowledged", false), (device.State, device.Acknowledged));
        Assert.Equal(f.Pending(pending).Deadline, device.ExpiresAt);
        // Every repository transaction replaces the gate with the next sequence.
        Assert.True(sequences.Count >= 3);
        Assert.Equal(Enumerable.Range((int)sequences[0], sequences.Count).Select(n => (long)n), sequences);
    }

    [Fact]
    public async Task Existing_locator_of_another_enrollment_is_never_overwritten_or_delivered()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var enrollmentId = f.Pending(pending).EnrollmentId!;
        var other = new EnrollmentLocator { RepositoryId = "654321", EnrollmentId = enrollmentId, PendingId = "654321_" + Guid.NewGuid().ToString("N") };
        f.State.Seed(EnrollmentLocator.Key(enrollmentId), other);
        var completion = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, completion.StatusCode);
        Assert.DoesNotContain("deviceSecret", completion.Text);
        Assert.Equal("654321", f.State.Get(EnrollmentLocator.Key(enrollmentId))!.RepositoryId);
    }

    [Fact]
    public async Task Completed_enrollment_that_lost_its_binding_is_never_delivered_again()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = await f.Complete(pending);
        var device = completion.Json.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, device), row => row["AppSlug"] = "another-app");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = f.GitHub.Reads;
        var retry = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.DoesNotContain("deviceSecret", retry.Text);
        Assert.Equal(reads, f.GitHub.Reads);
    }

    [Fact]
    public async Task Withdrawn_approval_closes_the_completed_owner_with_a_revocation_receipt()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = await f.Complete(pending);
        var device = completion.Json.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = completion.Json.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        f.GitHub.Marker = "";
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.DoesNotContain("deviceSecret", (await f.Complete(pending)).Text);
        Assert.Equal(("closed", "closed"), (f.Pending(pending).State, f.Gate.State));
        Assert.Equal("revoked", f.Enrollment(device).State);
        var receipt = f.State.Get(RevocationReceipt.Key(InMemoryBroker.RepositoryId, device))!;
        Assert.Equal((InMemoryBroker.RepositoryId, device, InMemoryBroker.Epoch), (receipt.RepositoryId, receipt.EnrollmentId, receipt.Epoch));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(device, completion.Json.GetProperty("completionId").GetString()!, secret);
        Assert.NotEqual(HttpStatusCode.OK, ack.StatusCode);
        Assert.False(f.Enrollment(device).Acknowledged);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("completion")]
    [InlineData("revoked")]
    public async Task Acknowledgment_needs_the_controller_secret_the_completion_ID_and_an_unacknowledged_enrollment(string fault)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = (await f.Complete(pending)).Json;
        var device = completion.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = completion.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var completionId = completion.GetProperty("completionId").GetString()!;
        if (fault == "revoked") f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, device), row => row["State"] = "revoked");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(device, fault == "completion" ? Guid.NewGuid().ToString("N") : completionId,
            fault == "secret" ? new string('b', 43) : secret);
        Assert.Equal(HttpStatusCode.Unauthorized, ack.StatusCode);
        Assert.False(f.Enrollment(device).Acknowledged);
        Assert.Equal("completed", f.Pending(pending).State);
    }

    [Fact]
    public async Task Acknowledgment_activates_the_pending_enrollment()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = (await f.Complete(pending)).Json;
        var device = completion.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(device, completion.GetProperty("completionId").GetString()!,
            completion.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.Equal(("active", "active", "active"), (f.Pending(pending).State, f.Gate.State, f.Enrollment(device).State));
        Assert.Equal(f.Clock.GetUtcNow(), f.Pending(pending).FirstTerminalAt);
    }

    [Theory]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("binding", HttpStatusCode.ServiceUnavailable)]
    public async Task Lost_acknowledgment_retry_answers_only_for_an_active_bound_enrollment(string fault, HttpStatusCode expected)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = (await f.Complete(pending)).Json;
        var device = completion.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = completion.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var completionId = completion.GetProperty("completionId").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(device, completionId, secret)).StatusCode);
        if (fault == "expired") f.Clock.Advance(TimeSpan.FromDays(91));
        else f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, device), row => row["AppSlug"] = "another-app");
        Assert.Equal(expected, (await f.Acknowledge(device, completionId, secret)).StatusCode);
    }

    [Fact]
    public async Task Status_after_the_deadline_is_expired()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        f.Clock.Advance(f.Pending(pending).Deadline!.Value - f.Clock.GetUtcNow());
        var status = await f.Status(pending);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("expired", status.Json.GetProperty("state").GetString());
    }

    // The incumbent's own verifier settled, so the gate counts only the new pending enrollment's verifier.
    [Fact]
    public async Task New_discovery_cannot_take_over_an_incumbent_whose_tokens_settled()
    {
        var f = new InMemoryBroker();
        var first = await Start(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Complete(first)).StatusCode);
        var incumbent = f.Gate.OwnerEnrollmentId;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await Start(f);
        f.State.Change(RepositoryGate.Key(InMemoryBroker.RepositoryId), row => row["OutstandingTokenCount"] = 1L);
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(second)).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Equal(incumbent, f.Gate.OwnerEnrollmentId);
    }

    [Theory]
    [InlineData("VerifierAttempt")]
    [InlineData("State")]
    public async Task Completed_enrollment_is_delivered_again_only_while_the_gate_still_holds_it(string field)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Complete(pending)).StatusCode);
        f.State.Change(RepositoryGate.Key(InMemoryBroker.RepositoryId), row => row[field] = field == "State" ? "closed" : Guid.NewGuid().ToString("N"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = f.GitHub.Reads;
        var retry = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.DoesNotContain("deviceSecret", retry.Text);
        Assert.Equal(reads, f.GitHub.Reads);
    }

    // The controller enrollment is rewritten consistently with another installation: its own marker still matches its
    // binding, but not the pending enrollment's.
    [Fact]
    public async Task Completed_enrollment_of_another_binding_is_never_delivered()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = await f.Complete(pending);
        var device = completion.Json.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var p = f.Pending(pending);
        var moved = ApprovalMarker.Create(ApprovalContract.Legacy, InMemoryBroker.RepositoryId, p.Repository!, 790, device, p.Challenge,
            p.Profile!, InMemoryBroker.Epoch, p.Variable!, p.Environment, p.EnvironmentId);
        f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, device), row =>
        {
            row["InstallationId"] = 790L;
            row["Marker"] = moved;
        });
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = f.GitHub.Reads;
        var retry = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.DoesNotContain("deviceSecret", retry.Text);
        Assert.Equal(reads, f.GitHub.Reads);
    }

    private static async Task<string> Start(InMemoryBroker f)
    {
        var start = await f.Start(InMemoryBroker.Request());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        f.GitHub.Marker = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return start.Json.GetProperty("pendingId").GetString()!;
    }
}
