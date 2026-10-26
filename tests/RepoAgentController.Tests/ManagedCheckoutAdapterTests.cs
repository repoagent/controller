using System.Diagnostics;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// What the managed checkout adapter itself refuses or allows on the files it reads: their modes, symbolic links on its
/// paths, and the isolation of the clone it starts. Real Git and files; only GitHub is replaced, and no agent runtime runs.
/// </summary>
[Collection("Managed checkout environment")]
public sealed class ManagedCheckoutAdapterTests
{
    const string Unsafe = "Managed checkout blocked: private state is missing or unsafe; preserve this workspace and inspect it before retrying";
    const string Linked = "Managed checkout blocked: symbolic links are not supported in managed paths; preserve this workspace and inspect it before retrying";
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static async Task<CheckoutFixture> Cloned()
    {
        var fixture = new CheckoutFixture();
        await fixture.Start();
        using (await fixture.Open()) { }
        return fixture;
    }

    public static TheoryData<string> Unsafe_files() => ["record-group-readable", "marker-other-executable", "lock-group-writable"];

    [Theory]
    [MemberData(nameof(Unsafe_files))]
    public async Task A_restart_refuses_a_checkout_file_open_to_other_users_and_changes_nothing(string scenario)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await Cloned();
        if (scenario == "record-group-readable") File.SetUnixFileMode(fixture.Record, Private | UnixFileMode.GroupRead);
        if (scenario == "marker-other-executable") File.SetUnixFileMode(fixture.Marker, Private | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        if (scenario == "lock-group-writable") File.SetUnixFileMode(fixture.Lock, Private | UnixFileMode.GroupWrite);
        var before = fixture.Snapshot();

        Assert.Equal(Unsafe, (await Assert.ThrowsAsync<IOException>(() => fixture.Open())).Message);

        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task A_restart_accepts_checkout_files_executable_by_their_owner_only()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await Cloned();
        // An executable SMB view reports owner execute on every file.
        foreach (var path in new[] { fixture.Record, fixture.Lock, fixture.Marker }) File.SetUnixFileMode(path, Private | UnixFileMode.UserExecute);
        var before = fixture.Snapshot();

        using (var checkout = await fixture.Open()) Assert.Equal(fixture.Root, checkout.Root);

        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(true, "owned checkout is missing")]
    [InlineData(false, "checkout has no ownership record")]
    public async Task A_restart_refuses_a_file_in_place_of_the_checkout_root_and_changes_nothing(bool recorded, string reason)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await Cloned();
        Directory.Move(fixture.Root, Path.Combine(fixture.Broker.Directory, "saved-root"));
        File.WriteAllText(fixture.Root, "not a checkout");
        if (!recorded) File.Delete(fixture.Record);
        var before = fixture.Snapshot();

        Assert.Equal("Managed checkout blocked: " + reason + "; preserve this workspace and inspect it before retrying",
            (await Assert.ThrowsAsync<IOException>(() => fixture.Open())).Message);

        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public void Git_output_is_read_as_Git_writes_it()
    {
        Assert.Equal(["core.bare", "branch.trunk.merge", "Remote.origin.url"],
            ManagedCheckout.CheckoutFacts.Keys("core.bare\nfalse\0branch.trunk.merge\nrefs/heads/trunk\nuser.name=x\0\0Remote.origin.url\nhttps://x\0"));
        Assert.Empty(ManagedCheckout.CheckoutFacts.Keys(""));
        Assert.Equal(["https://github.com/a.git", "https://github.com/b.git"], ManagedCheckout.CheckoutFacts.Lines("https://github.com/a.git\nhttps://github.com/b.git\n"));
        Assert.Equal(["https://github.com/a.git"], ManagedCheckout.CheckoutFacts.Lines("https://github.com/a.git \n\n"));
        Assert.Equal([""], ManagedCheckout.CheckoutFacts.Lines(""));
        Assert.Equal(
            [new("tracked", false, false), new("assumed", false, true), new("skipped", true, false), new("both", true, true),
                new("merged", false, false), new("assumed other", false, true), new("changed", false, false)],
            ManagedCheckout.CheckoutFacts.Entries("H tracked\0h assumed\0S skipped\0s both\0M merged\0c assumed other\0C changed\0"));
    }

    public static TheoryData<string> Links() => ["root-symlink", "index-symlink"];

    [Theory]
    [MemberData(nameof(Links))]
    public async Task A_restart_refuses_a_symbolic_link_on_a_checkout_path_and_changes_nothing(string scenario)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = await Cloned();
        if (scenario == "root-symlink")
        {
            var target = Path.Combine(fixture.Broker.Directory, "saved-root");
            Directory.Move(fixture.Root, target);
            Directory.CreateSymbolicLink(fixture.Root, target);
        }
        else
        {
            var index = Path.Combine(fixture.Root, ".git", "index");
            var target = Path.Combine(fixture.Broker.Directory, "saved-index");
            File.Move(index, target);
            File.CreateSymbolicLink(index, target);
        }
        var before = fixture.Snapshot();

        Assert.Equal(Linked, (await Assert.ThrowsAsync<IOException>(() => fixture.Open())).Message);

        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task The_agents_clone_runs_the_managed_clone_over_https_with_its_own_git()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new CheckoutFixture();
        await fixture.Start();
        // A hostile git first on PATH; the managed clone uses its own, and the gh stand-in asserts the HTTPS-only parent policy.
        var bin = Path.Combine(fixture.Broker.Directory, "hostile");
        Directory.CreateDirectory(bin);
        var hostile = Path.Combine(bin, "git");
        File.WriteAllText(hostile, "#!/bin/sh\nexit 97\n");
        CheckoutFixture.Executable(hostile);
        var clone = Path.Combine(fixture.Broker.Directory, "agent-clone");
        var start = new ProcessStartInfo(Path.Combine(fixture.Broker.CredentialDirectory, "bin", "gh"))
            { ArgumentList = { "repo", "clone", "https://github.com/owner/repository.git", clone }, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using (var live = fixture.Broker.Credentials())
        {
            await live.Initialize(null, TestContext.Current.CancellationToken);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
        }

        Assert.Equal("approved repository\n", File.ReadAllText(Path.Combine(clone, "tracked-sentinel")));
        Assert.Equal("https://github.com/owner/repository.git", await CheckoutFixture.Git("-C", clone, "remote", "get-url", "origin"));
        Assert.Equal(fixture.Commit, await CheckoutFixture.Git("-C", clone, "rev-parse", "HEAD"));
    }
}
