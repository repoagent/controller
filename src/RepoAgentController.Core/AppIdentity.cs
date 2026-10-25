using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>A GitHub account as GitHub reports an author or sender: its login and type, such as User or Bot.</summary>
public sealed record Actor(string Login, string Type);

/// <summary>
/// The controller's GitHub App, and what that identity decides about Discussion content: which messages the App wrote,
/// which ones ask it to enroll a Discussion, and which Discussion is its own dashboard. Before the App is initialized
/// (<paramref name="AppSlug"/> empty) no actor is the App.
/// </summary>
public sealed record AppIdentity(string AppSlug, MentionUser? MentionUser = null)
{
    /// <summary>Hidden in the rendered dashboard body; with App authorship it identifies the controller-owned dashboard.</summary>
    public const string DashboardMarker = "<!-- repoagent:controller-dashboard -->";

    public string AgentLogin => AppSlug.Length == 0 ? "" : AppSlug + "[bot]";

    /// <summary>GitHub reports the App's own messages either as its [bot] login or as a Bot with the bare slug.</summary>
    public bool IsAgentActor(Actor? actor) => AppSlug.Length != 0 && actor is not null &&
        (string.Equals(actor.Login, AgentLogin, StringComparison.OrdinalIgnoreCase) ||
         (actor.Type == "Bot" && string.Equals(actor.Login, AppSlug, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A participant's message that mentions the App by login, or the configured mention user as GitHub rendered it.</summary>
    public bool IsEnrollmentMention(Actor? author, string body, IReadOnlySet<string> mentionedUsers) => !IsAgentActor(author) &&
        ((MentionUser?.IsMentioned(mentionedUsers) ?? false) ||
         Regex.IsMatch(body, @"(?<![\w-])@" + Regex.Escape(AgentLogin) + @"(?![\w-])", RegexOptions.IgnoreCase));

    /// <summary>The controller's own status Discussion: authored by this App and marked. Participants cannot author as the App.</summary>
    public bool IsDashboard(Actor? author, string body) => IsAgentActor(author) && body.Contains(DashboardMarker, StringComparison.Ordinal);
}
