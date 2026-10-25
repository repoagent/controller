using Xunit;

namespace RepoAgentController.Tests;

// What the App's identity decides about Discussion content, in the core: who is the App, which messages ask it to enroll a
// Discussion, and which Discussion is its dashboard. Which users GitHub's rendered HTML mentions is the adapter's
// (RenderedMentions), checked under Native AOT by NativeSmoke.
public sealed class AppIdentityTests
{
    static readonly AppIdentity App = new("test-app");
    static readonly AppIdentity WithMentionUser = App with { MentionUser = new("repoagent") };
    static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Theory]
    [InlineData("test-app[bot]", "Bot", true)]
    [InlineData("Test-App[BOT]", "Bot", true)]
    [InlineData("test-app[bot]", "User", true)]
    [InlineData("test-app", "Bot", true)] // GitHub reports some App-authored nodes as a Bot with the bare slug.
    [InlineData("TEST-APP", "Bot", true)]
    [InlineData("test-app", "User", false)] // A person may own the slug as a login.
    [InlineData("test-app-other[bot]", "Bot", false)]
    [InlineData("participant", "User", false)]
    public void The_App_is_its_bot_login_or_a_Bot_with_its_slug(string login, string type, bool isApp) =>
        Assert.Equal(isApp, App.IsAgentActor(new Actor(login, type)));

    [Fact]
    public void Before_the_App_is_known_no_actor_is_the_App()
    {
        var uninitialized = new AppIdentity("");
        Assert.Equal("", uninitialized.AgentLogin);
        Assert.False(uninitialized.IsAgentActor(new Actor("[bot]", "Bot")));
        Assert.False(uninitialized.IsAgentActor(new Actor("", "Bot")));
        Assert.False(App.IsAgentActor(null));
    }

    [Theory]
    [InlineData("@test-app[bot] please review this.", true)]
    [InlineData("Could @Test-App[BOT] look at it?", true)]
    [InlineData("(@test-app[bot])", true)]
    [InlineData("Thanks @test-app[bot].", true)]
    [InlineData("@test-app[bot]s are useful", false)]
    [InlineData("@test-app[bot]-like tools", false)]
    [InlineData("mail@test-app[bot] or x-@test-app[bot]", false)]
    [InlineData("@test-app please", false)] // Only the [bot] login mentions the App in text.
    [InlineData("No mention here.", false)]
    public void A_participant_mentions_the_App_by_its_bot_login_as_a_whole_word(string body, bool enrolls) =>
        Assert.Equal(enrolls, App.IsEnrollmentMention(new("participant", "User"), body, None));

    [Theory]
    [InlineData("repoagent", true)]
    [InlineData("RepoAgent", true)]
    [InlineData("repoagent-other", false)]
    [InlineData("egil", false)]
    public void The_configured_mention_user_enrolls_only_when_GitHub_rendered_a_mention_of_that_exact_login(string mentioned, bool enrolls)
    {
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mentioned };
        // The body text alone does not count: GitHub renders no mention link inside code, for example.
        Assert.Equal(enrolls, WithMentionUser.IsEnrollmentMention(new("participant", "User"), "`@repoagent` please", users));
        Assert.True(new MentionUser("repoagent").IsMentioned(users) == enrolls);
        // Without a configured mention user, a rendered mention of anyone enrolls nothing.
        Assert.False(App.IsEnrollmentMention(new("participant", "User"), "please", users));
    }

    [Fact]
    public void The_App_never_enrolls_a_Discussion_by_mentioning_itself_or_the_mention_user()
    {
        var mentioned = new HashSet<string> { "repoagent" };
        Assert.False(WithMentionUser.IsEnrollmentMention(new("test-app[bot]", "Bot"), "@test-app[bot] status", mentioned));
        Assert.False(WithMentionUser.IsEnrollmentMention(new("test-app", "Bot"), "@test-app[bot] status", mentioned));
        Assert.True(WithMentionUser.IsEnrollmentMention(null, "@test-app[bot] status", None)); // A deleted account's message.
    }

    [Theory]
    [InlineData("test-app[bot]", "Bot", AppIdentity.DashboardMarker + "\nOperational status", true)]
    [InlineData("test-app", "Bot", "Status\n" + AppIdentity.DashboardMarker, true)]
    [InlineData("participant", "User", AppIdentity.DashboardMarker + "\nOperational status", false)] // A participant's copy.
    [InlineData("test-app[bot]", "Bot", "Operational status", false)]
    [InlineData("test-app[bot]", "Bot", "<!-- REPOAGENT:CONTROLLER-DASHBOARD -->", false)]
    public void The_dashboard_is_the_App_authored_Discussion_that_carries_the_marker(string login, string type, string body, bool isDashboard) =>
        Assert.Equal(isDashboard, App.IsDashboard(new Actor(login, type), body));
}
