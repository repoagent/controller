using RepoAgentController;
using Xunit;

public sealed class MentionUserTests
{
    // Shapes captured from GitHub's POST /markdown in GFM repository context.
    [Theory]
    [InlineData("<p><a class=\"user-mention notranslate\" href=\"https://github.com/repoagent\">@repoagent</a></p>")]
    [InlineData("<strong><a class=\"user-mention notranslate\" href=\"https://github.com/repoagent\">@repoagent</a></strong>")]
    public void A_rendered_mention_targets_the_configured_user(string html)
    {
        Assert.True(new MentionUser("repoagent").IsMentioned(html));
    }

    [Theory]
    [InlineData("<p><code>@repoagent</code></p>")]
    [InlineData("<pre><code>@repoagent</code></pre>")]
    [InlineData("<a href=\"mailto:support+@repoagent.example\">support+@repoagent.example</a>")]
    [InlineData("<a href=\"https://github.com/repoagent\">profile</a>")]
    [InlineData("<a class=\"user-mention\" href=\"https://github.com/egil\">@egil</a>")]
    [InlineData("<a class=\"user-mention\" href=\"https://github.com/repoagent-other\">@repoagent-other</a>")]
    [InlineData("<a class=\"user-mention\" href=\"https://github.com.evil/repoagent\">@repoagent</a>")]
    [InlineData("<p>@repoagent</p>")]
    public void Other_rendered_content_does_not_enroll_a_discussion(string html)
    {
        Assert.False(new MentionUser("repoagent").IsMentioned(html));
    }
}
