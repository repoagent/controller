using Xunit;

namespace RepoAgentController.Tests;

// How the managed checkout's refresh journal reads a turn's identity from an agent runtime event. A malformed identity blocks
// checkout refresh with this text as its reason; the journal itself runs only with a managed checkout (tests/installers).
public sealed class CheckoutRefreshLifecycleTests
{
    [Fact]
    public void A_turn_event_names_its_thread_and_turn_and_a_missing_or_empty_identity_is_malformed()
    {
        Assert.Equal(("S1", "T1", false), CheckoutRefresh.Lifecycle(new AgentTurnStarted(1, "S1", "T1")));
        Assert.Equal(("S1", "T1", true), CheckoutRefresh.Lifecycle(new AgentTurnCompleted(1, "S1", "T1", AgentTurnStatus.Completed, null)));
        Assert.Null(CheckoutRefresh.Lifecycle(new AgentSessionStatusChanged(1, "S1", AgentSessionStatus.Idle)));

        // A missing turn ID reads as empty, which no turn identity is.
        AgentRuntimeEvent[] malformed = [new AgentTurnStarted(1, "S1", null), new AgentTurnCompleted(1, "S1", null, AgentTurnStatus.Completed, null),
            new AgentTurnStarted(1, "", "T1"), new AgentTurnCompleted(1, "S1", new string('t', 257), AgentTurnStatus.Completed, null)];
        Assert.All(malformed, change => Assert.Equal("Malformed native lifecycle identity",
            Assert.Throws<IOException>(() => CheckoutRefresh.Lifecycle(change)).Message));
    }
}
