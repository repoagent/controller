using AngleSharp.Html.Parser;

namespace RepoAgentController;

/// <summary>A real GitHub user used as a mention target, independently of the publishing App.</summary>
public sealed record MentionUser(string Login)
{
    public bool IsMentioned(string renderedHtml)
    {
        using var document = new HtmlParser().ParseDocument(renderedHtml);
        return document.QuerySelectorAll("a.user-mention").Any(link =>
            string.Equals(link.GetAttribute("href"), "https://github.com/" + Login, StringComparison.OrdinalIgnoreCase));
    }
}
