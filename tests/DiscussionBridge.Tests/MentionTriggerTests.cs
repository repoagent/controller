using DiscussionBridge;
using Xunit;

public sealed class MentionTriggerTests
{
    [Theory]
    [InlineData("@bridge please check this")]
    [InlineData("Could you check this, @BRIDGE?")]
    [InlineData("(@bridge), thanks")]
    [InlineData("@discussion-agent-bridge[bot] please check this")]
    [InlineData("Thanks, @DISCUSSION-AGENT-BRIDGE[BOT]!")]
    public void A_configured_short_name_and_the_authenticated_App_login_both_trigger_the_agent(string text)
    {
        var trigger = new MentionTrigger("discussion-agent-bridge[bot]", "bridge");

        Assert.Equal("bridge", trigger.Name);
        Assert.True(trigger.Matches(text));
    }

    [Theory]
    [InlineData("Contact someone@bridge for help")]
    [InlineData("Ask @bridgekeeper instead")]
    [InlineData("Ask @bridge-other instead")]
    [InlineData("Ask @bridge_other instead")]
    [InlineData("Ask @@bridge instead")]
    [InlineData("Ask @bridge[bot] instead")]
    [InlineData("Ask @another-agent[bot] instead")]
    [InlineData("Ask @discussion-agent-bridge[bot]-other instead")]
    public void Names_embedded_in_addresses_or_other_actor_mentions_do_not_trigger_the_agent(string text)
    {
        var trigger = new MentionTrigger("discussion-agent-bridge[bot]", "bridge");

        Assert.False(trigger.Matches(text));
    }

    [Fact]
    public void Omitting_the_short_name_keeps_the_authenticated_App_login_as_the_trigger()
    {
        var trigger = new MentionTrigger("discussion-agent-bridge[bot]");

        Assert.Equal("discussion-agent-bridge[bot]", trigger.Name);
        Assert.True(trigger.Matches("Hello @discussion-agent-bridge[bot]"));
        Assert.False(trigger.Matches("Hello @bridge"));
        Assert.False(trigger.Matches("Hello @discussion-agent-bridge"));
    }

    [Fact]
    public void Short_names_support_the_minimum_and_maximum_lengths_and_internal_hyphens()
    {
        foreach (var name in new[] { "a", "a-1", new string('b', 39) })
        {
            var trigger = new MentionTrigger("discussion-agent-bridge[bot]", name);
            Assert.True(trigger.Matches("Please @" + name + "!"));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("@bridge")]
    [InlineData("-bridge")]
    [InlineData("bridge-")]
    [InlineData("bridge_name")]
    [InlineData("brídge")]
    [InlineData("bridge name")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789abcd")]
    public void Invalid_short_names_are_rejected_at_configuration_time(string name)
    {
        Assert.Throws<ArgumentException>(() => new MentionTrigger("discussion-agent-bridge[bot]", name));
    }
}
