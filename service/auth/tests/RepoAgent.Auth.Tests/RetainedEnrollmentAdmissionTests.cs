using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RetainedEnrollmentAdmissionTests
{
    [Fact]
    public async Task Retained_enrollment_limit_denies_new_start_without_charging_identical_retry()
    {
        var f = new InMemoryBroker { Retained = new(128, 1, 4096) };
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var request = new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling");
        var first = await f.Start(request, 2);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var retry = await f.Start(request, 2);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(first.Text, retry.Text);
        Assert.Equal(1, f.GitHub.VerifierMints);

        // Remove the independent per-minute mint ceiling as a reason for denial.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await f.Start(new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL_SECOND", "polling"), 2);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        // Fresh reads expose any leaked reservation or repository publication on denial.
        var rows = f.Rows();
        Assert.Equal("pending:" + request.IdempotencyId,
            Assert.Single(rows, row => row.Scope == "global:enrollment-v1" && row.Name.StartsWith("pending:", StringComparison.Ordinal)).Name);
        Assert.Equal("pending:" + request.IdempotencyId,
            Assert.Single(rows, row => row.Scope == InMemoryBroker.Partition && row.Name.StartsWith("pending:", StringComparison.Ordinal)).Name);
        var ledger = f.State.Get(EnrollmentLedger.Key(request.IdempotencyId))!;
        Assert.Equal(ledger.CreatedAt!.Value.AddMinutes(15), ledger.Deadline);
        Assert.Equal(ledger.Deadline!.Value.AddHours(24), ledger.PayloadPurgeAt);
        Assert.Equal(8, ledger.RowCredits);
        Assert.Equal(1, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }
}
