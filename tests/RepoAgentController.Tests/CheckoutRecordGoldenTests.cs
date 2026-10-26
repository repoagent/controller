using System.Text.Json.Nodes;
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

    // A record no writer leaves: the typed record writes its keys in the writers' order, then the unknown key as read.
    [Fact]
    public async Task A_refresh_over_a_record_in_another_order_writes_the_writers_order_and_keeps_the_unknown_key()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        File.WriteAllText(fixture.Record, Reordered(fixture, owner, fixture.Commit));
        var target = await fixture.Advance("advanced");

        using (var checkout = await fixture.Open()) Assert.True(await checkout.Refresh((_, _) => { }, Token));

        Assert.Equal(Written(fixture, owner, target)[..^1] + ",\"later\":{\"kept\":[1,\"two\"]}}", File.ReadAllText(fixture.Record));
    }

    // Records no writer leaves, which a restart failed on with an unhandled JSON exception before the record was typed. A
    // restart now refuses each with a managed checkout refusal and changes nothing.
    [Theory]
    [InlineData("not JSON", "invalid ownership record")]
    [InlineData("a list", "invalid ownership record")]
    [InlineData("a number", "invalid ownership record")]
    [InlineData("text", "invalid ownership record")]
    [InlineData("key repeated", "invalid ownership record")]
    [InlineData("key repeated differently", "invalid ownership record")]
    [InlineData("unknown key repeated", "invalid ownership record")]
    [InlineData("commit number", "invalid ownership record")]
    [InlineData("version text", "ownership or repository binding differs")]
    [InlineData("version 1.0", "ownership or repository binding differs")]
    [InlineData("state number", "ownership or repository binding differs")]
    [InlineData("root number", "ownership or repository binding differs")]
    [InlineData("owner number", "ownership or repository binding differs")]
    [InlineData("repositoryId text", "ownership or repository binding differs")]
    [InlineData("repositoryId 345.0", "ownership or repository binding differs")]
    [InlineData("repository number", "ownership or repository binding differs")]
    [InlineData("branch number", "ownership or repository binding differs")]
    public async Task A_restart_refuses_a_record_of_a_type_no_writer_leaves_and_changes_nothing(string form, string reason)
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        var written = Written(fixture, owner, fixture.Commit);
        string Replaced(string key, string json) { var record = JsonNode.Parse(written)!.AsObject(); record[key] = JsonNode.Parse(json); return record.ToJsonString(); }
        File.WriteAllText(fixture.Record, form switch
        {
            "not JSON" => "{", "a list" => "[]", "a number" => "5", "text" => "\"x\"",
            "key repeated" => written[..^1] + ",\"version\":1}",
            "key repeated differently" => written[..^1] + ",\"branch\":\"main\"}",
            "unknown key repeated" => written[..^1] + ",\"later\":1,\"later\":2}",
            "commit number" => Replaced("commit", "5"), "version text" => Replaced("version", "\"1\""), "version 1.0" => Replaced("version", "1.0"),
            "state number" => Replaced("state", "5"), "root number" => Replaced("root", "5"), "owner number" => Replaced("owner", "5"),
            "repositoryId text" => Replaced("repositoryId", "\"345\""), "repositoryId 345.0" => Replaced("repositoryId", "345.0"),
            "repository number" => Replaced("repository", "5"), _ => Replaced("branch", "5"),
        });
        var before = fixture.Snapshot();

        Assert.Equal("Managed checkout blocked: " + reason + "; preserve this workspace and inspect it before retrying",
            (await Assert.ThrowsAsync<IOException>(() => fixture.Open())).Message);

        Assert.Equal(before, fixture.Snapshot());
    }

    // Records no writer leaves that a restart accepted before the record was typed, and still does, leaving them as read.
    [Theory]
    [InlineData("commit missing")]
    [InlineData("commit null")]
    [InlineData("commit other")]
    public async Task A_restart_accepts_a_record_without_its_commit_and_leaves_it_as_read(string form)
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        var written = Written(fixture, owner, fixture.Commit);
        var recorded = form switch
        {
            "commit missing" => written[..written.IndexOf(",\"commit\"", StringComparison.Ordinal)] + "}",
            "commit null" => written.Replace("\"" + fixture.Commit + "\"", "null", StringComparison.Ordinal),
            _ => Written(fixture, owner, "elsewhere"),
        };
        File.WriteAllText(fixture.Record, recorded);
        var before = fixture.Snapshot();

        using (var checkout = await fixture.Open()) Assert.Equal(fixture.Root, checkout.Root);

        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task A_refresh_over_a_record_rewritten_in_another_order_with_the_same_values_applies()
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        var target = await fixture.Advance("advanced");

        using (var checkout = await fixture.Open())
        {
            File.WriteAllText(fixture.Record, Reordered(fixture, owner, fixture.Commit).Replace(",\"later\":{\"kept\":[1,\"two\"]}", ""));
            Assert.True(await checkout.Refresh((_, _) => { }, Token));
        }

        Assert.Equal(Written(fixture, owner, target), File.ReadAllText(fixture.Record));
    }

    // A record that is no longer JSON failed the refresh with an unhandled JSON exception before the record was typed.
    [Theory]
    [InlineData("not JSON")]
    [InlineData("commit other")]
    public async Task A_refresh_refuses_a_record_changed_since_the_checkout_opened_and_changes_nothing(string form)
    {
        if (OperatingSystem.IsWindows()) return;
        var (fixture, owner) = await Cloned();
        using var _ = fixture;
        await fixture.Advance("advanced");

        using var checkout = await fixture.Open();
        File.WriteAllText(fixture.Record, form == "not JSON" ? "{" : Written(fixture, owner, new string('2', 40)));
        var before = fixture.Snapshot();

        Assert.Equal("Managed checkout blocked: ownership record changed; preserve this workspace and inspect it before retrying",
            (await Assert.ThrowsAsync<IOException>(() => checkout.Refresh((_, _) => { }, Token))).Message);

        Assert.Equal(before, fixture.Snapshot());
    }
}
