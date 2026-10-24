using AngleSharp.Html.Parser;

namespace RepoAgentController;

/// <summary>Reads mentions from GitHub-rendered message HTML, where code, plain text and other links mention no one.</summary>
public static class RenderedMentions
{
    const string Profile = "https://github.com/";

    /// <summary>The logins that the user-mention links in <paramref name="renderedHtml"/> point to.</summary>
    public static IReadOnlySet<string> Users(string renderedHtml)
    {
        using var document = new HtmlParser().ParseDocument(renderedHtml);
        var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in document.QuerySelectorAll("a.user-mention"))
            if (link.GetAttribute("href") is { } href && href.StartsWith(Profile, StringComparison.OrdinalIgnoreCase)) users.Add(href[Profile.Length..]);
        return users;
    }
}
