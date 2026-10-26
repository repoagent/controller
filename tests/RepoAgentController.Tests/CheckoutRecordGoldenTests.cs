using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// The ownership record (.repoagent-checkout.json) as its writers leave it: a clone writes it, a refresh that applies a new
/// commit rewrites it, and a restart reads it and writes nothing. Real Git and files; only GitHub is replaced.
/// </summary>
[Collection("Managed checkout environment")]
public sealed class CheckoutRecordGoldenTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static string Written(CheckoutFixture fixture, string owner, string commit) =>
        "{\"version\":1,\"state\":\"" + fixture.Broker.State + "\",\"root\":\"" + fixture.Root + "\",\"owner\":\"" + owner +
        "\",\"repositoryId\":345,\"repository\":\"owner/repository\",\"remote\":\"https://github.com/owner/repository.git\",\"branch\":\"trunk\",\"commit\":\"" +
        commit + "\"}";

    // The keys in another order, and one this controller does not write.
    static string Reordered(CheckoutFixture fixture, string owner, string commit) =>
        "{\"commit\":\"" + commit + "\",\"later\":{\"kept\":[1,\"two\"]},\"branch\":\"trunk\",\"remote\":\"https://github.com/owner/repository.git\"," +
        "\"repository\":\"owner/repository\",\"repositoryId\":345,\"owner\":\"" + owner + "\",\"root\":\"" + fixture.Root + "\",\"state\":\"" +
        fixture.Broker.State + "\",\"version\":1}";

    static async Task<(CheckoutFixture Fixture, string Owner)> Cloned()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var fixture = new CheckoutFixture();
        await fixture.Start();
        using (await fixture.Open()) { }
        return (fixture, File.ReadAllText(fixture.Marker));
    }

    [Fact]
    public async Task A_clone_writes_the_record_with_its_binding_owner_and_commit()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;

        Assert.Matches("\\A[0-9a-f]{32}\\z", owner);
        Assert.Equal(Written(fixture, owner, fixture.Commit), File.ReadAllText(fixture.Record));
    }

    [Fact]
    public async Task A_refresh_that_applies_a_new_commit_rewrites_only_the_commit()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        var target = await fixture.Advance("advanced");

        using (var checkout = await fixture.Open()) Assert.True(await checkout.Refresh((_, _) => { }, Token));

        Assert.Equal(Written(fixture, owner, target), File.ReadAllText(fixture.Record));
    }

    [Fact]
    public async Task A_restart_accepts_a_record_with_its_keys_in_another_order_and_an_unknown_key_and_leaves_it_as_read()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        var recorded = Reordered(fixture, owner, fixture.Commit);
        File.WriteAllText(fixture.Record, recorded);

        using (var checkout = await fixture.Open()) Assert.False(await checkout.Refresh((_, _) => { }, Token));

        Assert.Equal(recorded, File.ReadAllText(fixture.Record));
    }

    [Fact]
    public async Task A_refresh_over_a_record_with_an_unknown_key_keeps_the_key()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        File.WriteAllText(fixture.Record, Reordered(fixture, owner, fixture.Commit));
        var target = await fixture.Advance("advanced");

        using (var checkout = await fixture.Open()) Assert.True(await checkout.Refresh((_, _) => { }, Token));

        Assert.Equal(Reordered(fixture, owner, target), File.ReadAllText(fixture.Record));
    }
}
