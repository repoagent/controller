using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class EnrollmentTests
{
    [Fact]
    public async Task Lost_start_and_completion_responses_recover_one_controller_enrollment_and_only_ack_enables_runtime()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        var start = await f.Start(request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = start.Json;
        var id = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var duplicate = await f.Start(request);
        Assert.Equal(pending.ToString(), duplicate.Json.ToString());
        Assert.Equal(1, f.GitHub.VerifierMints);
        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(id);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var delivered = completion.Json;
        var envelope = delivered.GetProperty("enrollment");
        var enrollmentId = envelope.GetProperty("deviceId").GetString()!;
        var secret = envelope.GetProperty("deviceSecret").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(enrollmentId, secret)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(id);
        Assert.Equal(delivered.ToString(), recovered.Json.ToString());
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completionId = delivered.GetProperty("completionId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completionId, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completionId, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Complete(id)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(enrollmentId, secret)).StatusCode);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Restored_epoch_denies_old_credentials_without_settling_capabilities_or_releasing_the_owner(bool revoked, bool unknownSend)
    {
        var f = new InMemoryBroker();
        f.GitHub.RuntimeLifetime = TimeSpan.FromMinutes(6);
        var old = await Retained.ActiveAgent(f);
        // The same stored binding and credential authorize issuance before the external epoch changes.
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);
        if (unknownSend)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(2));
            f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Provider send outcome unknown");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);
            Assert.Contains(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal)
                && row.Row.TryGetValue("State", out var state) && Equals(state, "sending"));
        }
        if (revoked)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.EnrollmentId, old.Secret)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);
        }
        // Pending occupancy can release independently; outstanding capabilities and publication remain charged.
        await Retained.ReclaimEnrollment(f, old.Id);
        var before = f.Rows();
        var provider = (f.GitHub.Authorizations, f.GitHub.Reads, f.GitHub.Mints, f.GitHub.Revokes);
        var commits = f.State.Commits;
        f.DeploymentEpoch = "restored-epoch";

        var denied = await f.Issue(old.EnrollmentId, old.Secret);
        await Retained.ReclaimEnrollment(f, old.Id);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Empty(denied.Text);
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(provider, (f.GitHub.Authorizations, f.GitHub.Reads, f.GitHub.Mints, f.GitHub.Revokes));
        Assert.Equal(before.Select(row => (row.Key, row.Version)), f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(old.EnrollmentId, f.Gate.OwnerEnrollmentId);
        Assert.NotNull(f.State.Get(EnrollmentLocator.Key(old.EnrollmentId)));
        if (revoked)
        {
            Assert.Equal("revoked", f.Enrollment(old.EnrollmentId).State);
            Assert.NotNull(f.State.Get(RevocationReceipt.Key(InMemoryBroker.RepositoryId, old.EnrollmentId)));
        }
    }

    [Fact]
    public async Task Pending_capacity_and_wrong_recovery_possession_never_allocate_more_provider_work()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        Assert.Equal(HttpStatusCode.OK, (await f.Start(request)).StatusCode);
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, new string('b', 43))).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        for (var i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.OK, (await Retained.HistoricalDiscovery(f, () => f.Start(InMemoryBroker.Request()))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Retained.HistoricalDiscovery(f, () => f.Start(InMemoryBroker.Request()))).StatusCode);
        Assert.Equal(4, f.GitHub.VerifierMints);
    }
}
