using Xunit;

namespace RepoAgentController.Tests;

// The same Bridge interface used by credential-file intake, with real session and checkout exclusion decisions.
public sealed class AgentCredentialReplacementTests
{
    [Fact]
    public async Task An_idle_controller_installs_credentials_while_the_runtime_is_stopped()
    {
        await using var world = new BridgeWorld();
        await world.Begin();
        var installed = false;
        Assert.True(await world.Bridge.ReplaceAgentCredentials(() => installed = true, world.Token));
        Assert.True(installed);
        Assert.Equal(2, world.Runtime.Epoch);
    }

    [Fact]
    public async Task A_running_turn_holds_replacement_until_its_correlated_completion()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] retain this turn");
        await world.Begin();
        Assert.True(await world.Poll());
        var installed = false;
        Assert.False(await world.Bridge.ReplaceAgentCredentials(() => installed = true, world.Token));
        Assert.False(installed);
        Assert.Equal(1, world.Runtime.Epoch);
        world.Finish(1);
        Assert.True(await world.Bridge.ReplaceAgentCredentials(() => installed = true, world.Token));
        Assert.True(installed);
        Assert.Equal(2, world.Runtime.Epoch);
        Assert.Null(world.Saved(1)!.Running);
    }

    [Fact]
    public async Task Unsettled_checkout_admission_holds_replacement_even_without_an_active_runtime_turn()
    {
        await using var world = new BridgeWorld();
        var journal = new InMemoryRefreshJournal();
        await using var refresh = new CheckoutRefresh(new ScriptedCheckout(), new("/workspace/repository", "owner", 345, "refs/heads/main"),
            journal, false, false, world.Clock, new RecordingLog());
        world.CheckoutRefresh = refresh;
        await world.Begin();
        using (var admission = await refresh.Admit(world.Token))
        {
            admission.Bind("S1");
            admission.Unconfirmed();
        }
        var installed = false;
        Assert.False(await world.Bridge.ReplaceAgentCredentials(() => installed = true, world.Token));
        Assert.False(installed);
        Assert.Equal(1, world.Runtime.Epoch);

    }

    [Fact]
    public async Task A_controller_that_has_stopped_admission_does_not_restart_for_replacement_credentials()
    {
        await using var world = new BridgeWorld();
        await world.Begin();
        await world.Bridge.StopAndDrain(world.Token);
        var installed = false;
        Assert.False(await world.Bridge.ReplaceAgentCredentials(() => installed = true, world.Token));
        Assert.False(installed);
        Assert.Equal(1, world.Runtime.Epoch);
    }
    [Fact]
    public async Task An_admission_that_cannot_prove_global_idle_refuses_credential_replacement()
    {
        await using var world = new BridgeWorld { Admission = new RecordingCheckoutAdmission() };
        await world.Begin();
        var installed = false;
        Assert.False(await world.Bridge.ReplaceAgentCredentials(() => installed = true, world.Token));
        Assert.False(installed);
        Assert.Equal(1, world.Runtime.Epoch);
    }
}
