using Xunit;

namespace RepoAgent.Auth.Tests;

// The fixed one-minute window issuance charges before any GitHub work: one admission and one App JWT signature, the
// GitHub reads the operation will make, and one mint unless it reuses a published lease.
public sealed class ChargedQuotaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_030);
    private static readonly long Window = Now.ToUnixTimeSeconds() / 60;

    private static RepositoryQuota Charged() => new() { Window = Window, Admissions = 1, Reads = 8, Signs = 1, Mints = 1, Slots = 0 };

    [Fact]
    public void A_charge_that_reaches_every_limit_exactly_is_admitted()
    {
        var quota = Charged();
        quota.Charge(Now, 8, admissionLimit: 2, readLimit: 16, signLimit: 2, mintLimit: 2);
        Assert.Equal((Window, 2L, 16L, 2L, 2L), (quota.Window, quota.Admissions, quota.Reads, quota.Signs, quota.Mints));
    }

    [Theory]
    [InlineData("admissions")]
    [InlineData("reads")]
    [InlineData("signs")]
    [InlineData("mints")]
    public void A_charge_over_any_limit_is_429_and_charges_nothing(string exceeded)
    {
        var quota = Charged();
        var denied = Assert.Throws<IssuanceDenied>(() => quota.Charge(Now, 8,
            admissionLimit: exceeded == "admissions" ? 1 : 2, readLimit: exceeded == "reads" ? 15 : 16,
            signLimit: exceeded == "signs" ? 1 : 2, mintLimit: exceeded == "mints" ? 1 : 2));
        Assert.Equal(429, denied.Status);
        Assert.Equal((1L, 8L, 1L, 1L), (quota.Admissions, quota.Reads, quota.Signs, quota.Mints));
    }

    [Fact]
    public void Reusing_a_published_lease_charges_no_mint()
    {
        var quota = Charged();
        quota.Charge(Now, 8, admissionLimit: 2, readLimit: 16, signLimit: 2, mintLimit: 1, mint: false);
        Assert.Equal((2L, 16L, 2L, 1L), (quota.Admissions, quota.Reads, quota.Signs, quota.Mints));
    }

    [Fact]
    public void A_new_window_starts_every_counter_again()
    {
        var quota = Charged();
        quota.Charge(Now.AddMinutes(1), 8, admissionLimit: 1, readLimit: 8, signLimit: 1, mintLimit: 1);
        Assert.Equal((Window + 1, 1L, 8L, 1L, 1L), (quota.Window, quota.Admissions, quota.Reads, quota.Signs, quota.Mints));
    }

    // A stored window ahead of this clock means a clock went back; counting again from zero would reopen spent capacity.
    [Fact]
    public void A_stored_window_ahead_of_the_clock_is_unavailable()
    {
        var quota = Charged();
        Assert.Equal(503, Assert.Throws<IssuanceDenied>(() =>
            quota.Charge(Now.AddMinutes(-1), 8, admissionLimit: 2, readLimit: 16, signLimit: 2, mintLimit: 2)).Status);
        Assert.Equal((Window, 1L, 8L, 1L, 1L), (quota.Window, quota.Admissions, quota.Reads, quota.Signs, quota.Mints));
    }
}
